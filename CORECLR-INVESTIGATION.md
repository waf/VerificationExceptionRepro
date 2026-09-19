# Notes for a CoreCLR investigation

A spurious generic constraint rejection on types emitted into an in-memory dynamic assembly. Every
figure here is measured on .NET 10.0.11, Windows 11, 16 cores, unless the text marks it as
inference. This repo holds the harnesses and the runners. See `README.md`.

Castle DynamicProxy showed the fault first
([castleproject/Core#648](https://github.com/castleproject/Core/issues/648)). It reproduces with no
Castle involved.

The mechanism is identified and measured. See [Root cause](#root-cause). In short: every type load
in a dynamic module runs a metadata save pass. That pass sorts and renumbers the GenericParam
table. The runtime caches GenericParam rids, and nothing invalidates them when they move.

## The fault

The runtime rejects a generic method on an emitted type. It names a type argument that the call
site never passed. Two forms appear. They are the same failure, caught at different points:

```
System.Security.VerificationException: Method B.MethodB2: type argument 'B1'
    violates the constraint of type parameter 'B2'.

System.TypeLoadException: GenericArguments[0], 'A2', on 'Invocations.B_MethodB2[B2]'
    violates the constraint of type parameter 'B2'.
```

The call site is `MethodB2<int>`. `int` satisfies the declared `where B2 : struct`.

**The reported type argument is never the one passed.** It is the name of a generic parameter that
belongs to a different generic method in the same dynamic module. Which one varies between runs:

- `B1`, the sibling generic method on the same type.
- `A1` or `A2`, generic methods on a different emitted type in the same module.

**The foreign name can appear on either side of the comparison.** One real-world occurrence
rejected a genuine type argument on a method that declares `TValue` with no constraint. It reported
the constraint owner as `T`, a name that does not exist on that method. An unconstrained parameter
cannot reject any type argument. The metadata under consultation therefore does not belong to the
method under verification.

**This is a rejection, not observably wrong execution.** Remove the constraint from the method and
the runs are clean, and the method observes the correct type argument. With the constraint present,
the runtime raises the exception before the body runs. What the body would have observed is
unknown.

## Reflection reports it without any exception

`src/ReflectionAudit` runs the same emit sequence. It then asks reflection what the emitted generic
parameters are. The emitter copies each parameter name from its template, so the correct answer is
known in advance. The parameter of `MethodA1` can only be `A1`.

After a concurrent window, many parameters come back as a different member's parameter, with that
member's constraints:

```
method s0BProxy....MethodB1_callback: expected 'B1' got 'B2'
    (attrs=NotNullableValueTypeConstraint, DefaultConstructorConstraint)
method s1AProxy....MethodA1_callback: expected 'A1' got 'B2'
    (attrs=NotNullableValueTypeConstraint, DefaultConstructorConstraint)
type   s0B_MethodB2_...:              expected 'B2' got 'B1' (attrs=None)
```

The runs below use 150 iterations and four emitted proxy types per module. Each range covers three
runs:

| mode | failures | mismatches |
| --- | --- | --- |
| serial (control) | 0/600 | 0/3600 |
| concurrent | 203-249/600 | 1404-1836/3600 |
| concurrent, prewarmed | 86-285/600 | 0-18/3600 |

This settles three things:

- **`MethodA1` and `MethodB1` are unconstrained.** They still come back carrying `B2`'s `struct`
  constraint. The metadata under consultation does not belong to the member under inspection. This
  matches the real-world `TValue` and `T` occurrence above, and it now reproduces.
- **Generic types are affected, not only generic methods.**
- **The serial control is clean, always.** It uses the same emit and the same audit, and releases
  the threads one at a time. The corruption needs concurrency. It persists, and ordinary reflection
  reads it back long afterwards.

`prewarmed` binds every generic parameter on one thread before the concurrent window. It almost
removes the mismatches. It leaves the invocation failures in the same band. This first looked like
two distinct faults. It is one fault. See
[The exception, not just its wording](#the-exception-not-just-its-wording), where failures track
the number of table-reordering sorts and the mismatch count does not.

Asking reflection for a parameter is what binds it. Any probe that reads a parameter before the
window destroys what it measures.

The GenericParam table's `Owner` column is a `TypeOrMethodDef` coded token. It is not monotonic in
rid order for this emit shape, in every module sampled, although ECMA-335 requires the table to be
sorted by `Owner`. The mechanism below explains this: each metadata capture sorts the table, and
rows emitted after the last capture leave it unsorted again.

## What is required to reproduce

Remove any one condition below and the failures go to zero. Condition 6 is the exception. See the
note under it.

1. **A runtime `AssemblyBuilder`.** Run the same emit sequence through `PersistedAssemblyBuilder`,
   save it, load it as a normal assembly, and it never fails. This is the sharpest discriminator.
   It is the reason to look at in-memory dynamic modules.
2. **Two or more emitted types in one dynamic module.** One type in the module never fails. The
   types need not be distinct classes. Two proxy types from the same class, in one module, are
   enough, at 62 failures in 1200.
3. **Concurrent first invocation.** Invoke the emitted methods one at a time on one thread and it
   never fails.
4. **A generic type closed over the calling method's own method-generic-parameter.** That is a
   TypeSpec `Inv<!!0>`, constructed inside the generic method, and then called. Construct it
   without calling into it and it never fails. Call the target directly, with no such type, and it
   never fails.
5. **An array-typed parameter.** `void M<T>(T[] args)` fails. `void M<T>(T args)` does not.
6. **A constraint on the generic parameter.** The constraint is what gives the runtime something to
   reject. `where T : struct` is the one exercised here.

   This condition is required for the exception, not for the fault. Remove the constraint and
   nothing throws. The reflection audit still reports the corruption at close to the same rate:
   1242 of 3600 parameters, against 1530 of 3600 at baseline. An unconstrained parameter has
   nothing to reject, so the wrong `TypeVarTypeDesc` goes unnoticed. Conditions 1 to 5 zero the
   mismatches as well as the failures. This one does not.

Generation does not have to overlap with JIT. Create every type before anything runs and the fault
still appears. A test host is not needed. A console application reproduces it.

## Rates

All types built first, then invoked concurrently, over 300 iterations:

| builder | 2 types | 4 | 8 |
| --- | --- | --- | --- |
| `AssemblyBuilder` | 13/600 | 270/1200 | 1286/2400 |
| `PersistedAssemblyBuilder` | 0/600 | 0/1200 | 0/2400 |

Counts are failed invocations out of attempts. The rate climbs steeply with the number of emitted
types that share a module.

## Shape of the emitted code

For each generic method on the base type, the emitter produces:

- An **override** on the proxy type. It is generic, with the same signature as the base method.
- A non-virtual **callback** on the proxy type. It is generic, and it calls the base method.
- One **generic invocation type**, closed over the override's `!!0`, deriving from a non-generic
  base. Its single method calls the callback.

Two input classes with two generic methods each give six emitted types: two proxy types and four
invocation types. DynamicProxy and the minimal emitter produce the same shape.

The generic parameter metadata for `where T : struct` is identical on the override, the callback,
and the invocation type:

```
GENERICPARAM B2 pos=0
  attrs=NotNullableValueTypeConstraint, DefaultConstructorConstraint
  constraints=[System.ValueType]
```

Render the same emit sequence to a PE image and the IL passes `ilverify`. This covers the persisted
rendering only. `ilverify` reads a PE image, and no one can hand it the in-memory output that
fails.

## Not part of the trigger

A minimal repro needs none of these. Leave them out when you narrow the fault further:

- `ldtoken` of a MethodSpec, or `MethodBase.GetMethodFromHandle`.
- A `Type[] { typeof(!!0) }` argument array, or any `SetGenericMethodArguments`-style call.
- Interceptors, or any Castle type.
- `[Serializable]`, static fields, type initialisers, custom modifiers.
- Additional interfaces on the emitted types.

## Root cause

Measured on an instrumented checked CoreCLR build, `release/10.0` at `0e09f85d7`. Every step below
is read from the source or observed at runtime.

The corruption is retroactive. The bindings are correct when the runtime makes them. Instrumenting
both binding sites, and asserting that `EnumNext` succeeded and that the GenericParam row's `Owner`
and `Number` match the member under construction, produced zero violations. Those runs held 288 to
342 mismatches. The fault happens afterwards. The GenericParam table is renumbered underneath the
caches that point into it.

### The sequence

1. Every type load in a dynamic module runs a metadata save pass, whether or not anyone is
   debugging. The path is `ClassLoader::NotifyLoad` to `Module::NotifyDebuggerLoad` to
   `Module::UpdateDynamicMetadataIfNeeded` to `ReflectionModule::CaptureModuleMetaDataToMemory`,
   all in `src/coreclr/vm/ceeload.cpp`. The comment at the call site reads *"Always capture
   metadata, even if no debugger is attached. If a debugger later attaches, it will use this
   data."*

2. That capture serialises the live scope. It sets `MDUpdateExtension` and calls
   `IMetaDataEmit::GetSaveSize(cssQuick, ...)`. `RegMeta::GetSaveSize`
   (`src/coreclr/md/compiler/regmeta_emit.cpp`) calls `PreSave()`. For that update mode, `PreSave`
   runs `CMiniMdRW::PreSaveFull()` (`src/coreclr/md/enc/metamodelrw.cpp`).

3. `PreSaveFull` sorts the GenericParam table by `Owner` and remaps tokens, through
   `STABLESORTER_WITHREMAP(GenericParam, Owner)`. Instrument that sort to compare the `Owner`
   column before and after. A 40 iteration run gives 21 of 24 rows changing position, on 19 of 21
   sort events. The sort renumbers rids wholesale.

4. It runs concurrently, against its own stated precondition.
   `CaptureModuleMetaDataToMemory` says *"Caller ensures serialization that guarantees that the
   metadata doesn't grow underneath us."* No such serialization exists. A 40 iteration run gives 21
   sort passes from 21 distinct threads, and 46 checked-build assert failures. 30 of those are
   `hMDUpdateMode.GetOriginalMDUpdateMode() == MDUpdateFull`, where another thread had already
   switched the scope into `MDUpdateExtension`. The other 16 are the matching `hr == S_OK` on
   release, reported as "someone changed the MDUpdateMode meanwhile".

5. Nothing tells the runtime. `Module::m_GenericParamToDescMap` is keyed by GenericParam rid. Each
   `TypeVarTypeDesc` stores its rid in `m_token`, and reads its name and its constraints from that
   row on demand. The VM registers no `IMapToken` remap handler on the emit scope, and nothing
   invalidates the map. Its only operations are `GetElement(rid)` and `AddElement(rid)`
   (`src/coreclr/vm/ceeload.h`). After a renumber, a cached rid addresses a different row. The type
   variable then reports another member's name and another member's constraints.

### Why the conditions behave as they do

- **`PersistedAssemblyBuilder` is immune** because it is not a `ReflectionModule`. The capture path
  never runs for it. The sharpest discriminator in this investigation now has an explanation.
- **Two or more types in one module** are needed before a reorder can land a rid on a foreign
  owner.
- **The rate climbs steeply with type count** because each load adds a capture, a sort, and another
  chance to overlap.
- **Serial is always clean** because captures never overlap, and each one completes before the next
  load reads.
- **The corruption persists** because the stale rid is baked into a long-lived `TypeVarTypeDesc`.
- **The constraint is not part of the fault.** It decides only whether anyone notices. See the
  `NO_CONSTRAINT` row above.

### Two defects, arguably

- `ReflectionModule::CaptureModuleMetaDataToMemory` runs unsynchronised on concurrent type loads,
  against a precondition that its own comment asserts.
- The runtime caches metadata rids, in `m_GenericParamToDescMap` and `TypeVarTypeDesc::m_token`,
  that the metadata layer is free to renumber. Nothing invalidates them, and nothing notifies the
  runtime of a remap.

Area labels: `area-TypeSystem-coreclr` and `area-Diagnostics-coreclr`.

### The exception, not just its wording

The renumbering explains the `VerificationException` itself, not only the wrong names in its text.
`MethodDesc::SatisfiesMethodConstraints` calls `tyvar->SatisfiesConstraints(..., thArg, ...)`. A
`TypeVarTypeDesc` reads its constraints from the row that its rid addresses now. A stale rid
therefore does more than mislabel a failure. It makes the runtime evaluate the wrong constraint
set. An unconstrained parameter starts enforcing `struct`, or an argument that should carry
`struct` reads as unconstrained and fails a genuine check. Either way the throw is real.

Row-moving sorts counted against outcomes, over 40 iterations, four proxy types per module:

| mode | sorts that moved rows | checked-build asserts | failures | mismatches |
| --- | --- | --- | --- | --- |
| serial | 0 | 0 | 0/160 | 0/960 |
| concurrent | 20 | 53 | 41/160 | 360/960 |
| concurrent, prewarmed | 14 | 34 | 25/160 | 0/960 |

**Serial performs no row-moving sort at all, and throws nothing.** Failures track the number of
sorts: 0, 20, and 14 against 0, 41, and 25. The mismatch count does not.

Prewarming lowers the failure count roughly in proportion to the sorts it removes. It also drives
mismatches to zero. It probably does this because it binds every parameter after the captures that
prewarming itself provokes, which leaves the audit reading a settled table. That step is not
measured.

The exception and the reflection mismatch are the same fault. The mismatch count is the worse proxy
for it, because it sees only corruption that survives to the end of the iteration.

### Hypotheses that were tested and failed

Recorded so that no one retries them:

- Concurrent reflection over generic methods alone, in a shared dynamic module, with no IL and no
  constraints. It does not reproduce the fault. Adding interleaved generic types to that synthetic
  shape does not reproduce it either. The capture path needs real type loads.
- The metadata read path being unlocked. Ruled out. `RegMeta::CreateNewMD` creates the read/write
  lock when thread safety is on, `Assembly::DefineEmitScope` turns thread safety on, and the lock
  propagates to the internal importer.
- Deferring the invocation types' `CreateType()`. It does not change the `Owner` column's
  monotonicity, because the emitter writes GenericParam rows at `DefineGenericParameters` time, not
  at `CreateType` time. Mismatch rates did not change.
- The `mdtGenericParam` case of `MDInternalRW::EnumInit` returning a wrong rid. This looked
  compelling. It is marked `//@todo: deal with non-sorted case.`, it binary-searches a column that
  is genuinely unsorted, and `CMiniMdRW::vSearchTable` carries
  `// @todo GENERICS: why is IsSorted not true for mdtGenericParam?` with its sortedness assert
  commented out. Instrumentation showed that it is not the fault here. The enumeration returns the
  right rows every time.

### Unrelated defects noticed in the same code

Neither defect causes this bug. Both are wrong as written:

- **`EnumNext`'s return value is ignored at both binding sites.** `HENUMInternal::EnumNext`
  (`src/coreclr/md/enc/rwutil.cpp`) returns `bool`, and leaves its out token untouched when the
  enumerator is exhausted. `tkTyPar` is an uninitialized stack local in
  `InstantiatedMethodDesc::SetupGenericMethodDefinition` (`src/coreclr/vm/genmeth.cpp`) and in
  `MethodTableBuilder::GatherGenericsInfo` (`src/coreclr/vm/methodtablebuilder.cpp`). Neither site
  checks the return, so a short enumeration would silently reuse the previous iteration's token.
- **The count and the iteration come from two independent enumerations.** `numGenericArgs` in
  `GatherGenericsInfo` comes from `ReadyToRun_TypeGenericInfoMap::GetGenericArgumentCount`
  (`src/coreclr/vm/readytoruninfo.cpp`). For a dynamic module, that function performs its own
  `EnumInit` and `EnumGetCount`. It does not use the enum that the caller then iterates.

### Reproducing the measurement

Build a checked CoreCLR and run any harness against it. The assert fires on its own, with no added
instrumentation:

```
corerun.exe ReflectionAudit.dll 40 2 auditall

Assert failure: hMDUpdateMode.GetOriginalMDUpdateMode() == MDUpdateFull
    ReflectionModule::CaptureModuleMetaDataToMemory
    Module::UpdateDynamicMetadataIfNeeded
    ClassLoader::NotifyLoad
```

Set `DOTNET_ContinueOnAssert=1` to keep the process alive and count the asserts. Pair a checked
CoreCLR with the `System.Private.CoreLib.dll` from the same build.

A different bug produces the same message text:
[dotnet/runtime#45600](https://github.com/dotnet/runtime/issues/45600). It is deterministic, it
involves no dynamic assembly, and .NET 6 fixed it. It is not this bug.

## Reproducing

```
./run-builder-comparison.sh        # AssemblyBuilder against PersistedAssemblyBuilder
./run-condition-matrix.sh          # every condition above, three harnesses
./run-reflection-audit.sh          # the fault through reflection, with the serial control
```

Individual conditions are compile-time:

```
cd src/MinimalEmitter
dotnet build -p:Variant=NO_SIBLING          # also SCALAR, NO_CONSTRAINT, OWN_GENERATOR,
                                            # NO_PROCEED, ONE_CLASS, ONE_TYPE, NO_INVOCATION
./bin/Debug/net10.0/MinimalEmitter.exe 600 1            # iterations, proxy types per class
./bin/Debug/net10.0/MinimalEmitter.exe 600 1 persisted  # same through PersistedAssemblyBuilder
```

`src/MinimalEmitter` is self-contained. It has no package references and no Castle, and it prints
the failure messages it collects. `src/ReflectionAudit` compiles its `Emitter.cs` and `Inputs.cs`
directly, so the same emit sequence drives both:

```
cd src/ReflectionAudit
dotnet build
./bin/Debug/net10.0/ReflectionAudit.exe 150 2 auditall            # concurrent
./bin/Debug/net10.0/ReflectionAudit.exe 150 2 auditall serial     # the control
./bin/Debug/net10.0/ReflectionAudit.exe 150 2 auditall prewarm    # bound before the window
./bin/Debug/net10.0/ReflectionAudit.exe 1 2 dumptable             # GenericParam rows in rid order
```

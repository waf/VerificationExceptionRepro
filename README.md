# VerificationException from dynamically emitted generic methods

Investigation material for [castleproject/Core#648](https://github.com/castleproject/Core/issues/648).

Under concurrency, a generic method on a dynamically emitted type can fail verification. The runtime
names a type argument that the call site never passed:

```
System.Security.VerificationException: Method B.MethodB2: type argument 'B1'
    violates the constraint of type parameter 'B2'.
```

The call site passes `int`. `B1` is the type parameter of a different generic method. The runtime
reads the type argument from a parameter that belongs to another method in the same dynamic module.

## Summary

- The cause is identified and measured on a checked CoreCLR build. See [Root cause](#root-cause).
- The builder decides whether the fault appears. A runtime `AssemblyBuilder` reproduces it.
  `PersistedAssemblyBuilder` never does.
- This repo holds four harnesses:
  - `CastleHarness`, the original Castle reproduction.
  - `FullEmitter`, the same bug through System.Reflection.Emit (SRE), with no Castle.
  - `MinimalEmitter`, a shorter form of the same emitter.
  - `ReflectionAudit`, the same emit sequence again. It reads the emitted generic parameters back
    through reflection and compares each one against the name it must have. They come back under
    another member's name, with another member's constraints. See
    [The fault without the exception](#the-fault-without-the-exception).
- Every emitter produces IL that `ilverify` accepts. This covers the persisted rendering only. See
  [IL verification](#il-verification).
- Generation does not have to overlap with JIT. Build every type first, then invoke concurrently,
  and the fault still appears.

`MinimalEmitter` puts N proxy types into one assembly. It finishes emitting, then invokes every type
concurrently. Only the builder differs between the rows.

Each count is failed invocations out of attempts:

| builder | 2 proxy types | 4 | 8 |
| --- | --- | --- | --- |
| `AssemblyBuilder` | 13/600 | 270/1200 | 1286/2400 |
| `PersistedAssemblyBuilder` | 0/600 | 0/1200 | 0/2400 |

```
./run-builder-comparison.sh            # ITER=300 by default
EMITTER=FullEmitter ./run-builder-comparison.sh
```

The persisted path never fails, at any proxy count. `FullEmitter` gives the same split.

## Root cause

Every type load in a dynamic module runs a metadata save pass. It runs whether or not a debugger is
attached:

```
ClassLoader::NotifyLoad
  -> Module::UpdateDynamicMetadataIfNeeded
    -> ReflectionModule::CaptureModuleMetaDataToMemory      vm/ceeload.cpp
      -> IMetaDataEmit::GetSaveSize -> CMiniMdRW::PreSaveFull   md/enc/metamodelrw.cpp
        -> STABLESORTER_WITHREMAP(GenericParam, Owner)
```

The last step sorts the GenericParam table and renumbers its rids. Instrumentation shows 21 of 24
rows moving per sort.

The runtime caches those rids in `Module::m_GenericParamToDescMap` and in
`TypeVarTypeDesc::m_token`. It reads each generic parameter's name and constraints from the row that
the rid points at now. No remap handler is registered, and nothing invalidates the map. The runtime
never learns that the rows moved.

Concurrent type loads run this capture without the serialization that its own comment claims. A
checked build confirms this: 46 assert failures in 40 iterations, 30 of them
`hMDUpdateMode.GetOriginalMDUpdateMode() == MDUpdateFull`.

The bindings are correct when the runtime makes them. The table moves underneath them afterwards. A
wrong parameter is therefore a stale rid, not a bad lookup. `PersistedAssemblyBuilder` is immune
because it is not a `ReflectionModule`, so the capture never runs for it.

This also accounts for the `VerificationException` itself, not only the wrong names in its text. A
`TypeVarTypeDesc` reads its constraints from the row that its rid addresses now. A stale rid
therefore makes the runtime enforce the wrong constraint set, and the check fails for real. Over 40
iterations, failures track the number of sorts that move rows. The mismatch count does not:

| mode | sorts that moved rows | failures | mismatches |
| --- | --- | --- | --- |
| serial | 0 | 0/160 | 0/960 |
| concurrent | 20 | 41/160 | 360/960 |
| concurrent, prewarmed | 14 | 25/160 | 0/960 |

[CORECLR-INVESTIGATION.md](CORECLR-INVESTIGATION.md) holds the full evidence, the failed
hypotheses, and two unrelated defects in the same code.

## Repository contents

| project | role |
| --- | --- |
| `src/MinimalEmitter` | reduced SRE emitter; drives the builder comparison and the `Minimal SRE repro` column |
| `src/FullEmitter` | tpflueger's emitter unreduced, same driver; the `SRE repro` column |
| `src/CastleHarness` | DynamicProxy from a console application; the `Castle repro` column |
| `src/CastleXunitHarness` | the issue's repro in its original xunit shape |
| `src/CastlePeCapture` | writes DynamicProxy's PE image for `ilverify` |
| `src/ReflectionAudit` | the same emit sequence; reads the emitted generic parameters back through reflection |

Both emitter projects share `Program.cs`, the runtime-versus-persisted driver. They differ only in
`Emitter.cs`, and in the invocation base class that their emitted types derive from.

## Comparing 3 reproductions in this repository

Each row changes one condition. All rows use the `A` and `B` classes from the issue, and two proxy
types per module.

| Condition changed | Castle repro | Type arg named | SRE repro | Type arg named | Minimal SRE repro | Type arg named |
| --- | --- | --- | --- | --- | --- | --- |
| none, baseline | 41/1200 | `B1` | 45/1200 | `B1` | 31/1200 | `B1` |
| `args` not an array (`SCALAR`) | 0/1200 | | 0/1200 | | 0/1200 | |
| constraint removed (`NO_CONSTRAINT`) | 0/1200 | | 0/1200 | | 0/1200 | |
| `MethodB1` deleted (`NO_SIBLING`) | 35/1200 | `A2` | 41/1200 | `A2` | 37/1200 | `A2` |
| a `ProxyGenerator` per type (`OWN_GENERATOR`) | 0/1200 | | 0/1200 | | 0/1200 | |
| non-proceeding interceptor (`NO_PROCEED`) | 0/1200 | | 0/1200 | | 0/1200 | |
| no concurrency (`ONE_CLASS`) | 0/1200 | | 0/1200 | | 0/1200 | |
| one proxy type per module (`ONE_TYPE`) | 0/600 | | 0/600 | | 0/600 | |

Conditions are compile-time. One build gives one condition: `dotnet build -p:Variant=NO_SIBLING`.
The variants are `SCALAR`, `NO_CONSTRAINT`, `NO_SIBLING`, `OWN_GENERATOR`, `NO_PROCEED`,
`ONE_CLASS`, `ONE_TYPE`, and `NO_INVOCATION`. `NO_INVOCATION` applies to the emitters only.

```
./run-condition-matrix.sh
ITER=200 VARIANTS="BASELINE NO_SIBLING" ./run-condition-matrix.sh
```

Every row agrees. The conditions are the five from the issue, plus the "only one type proxied"
observation from a later comment. Deleting `MethodB1` is the one addition here.

## The fault without the exception

`src/ReflectionAudit` compiles MinimalEmitter's `Emitter.cs` and `Inputs.cs` unchanged, so it runs
the same emit sequence. It then reads every emitted generic parameter back through reflection and
compares it against the name that parameter must have. It counts two things per run: the
invocation failures the runtime throws, and the parameters that come back under the wrong name.

The emitter copies each parameter name from the template it came from. The correct name of every
emitted parameter is therefore known in advance. The parameter of `MethodA1` can only be `A1`.

After a concurrent window, many parameters come back wrong:

```
method s0BProxy....MethodB1_callback: expected 'B1' got 'B2'
    (attrs=NotNullableValueTypeConstraint, DefaultConstructorConstraint)
method s1AProxy....MethodA1_callback: expected 'A1' got 'B2'
    (attrs=NotNullableValueTypeConstraint, DefaultConstructorConstraint)
type   s0B_MethodB2_...:              expected 'B2' got 'B1' (attrs=None)
```

`MethodA1` and `MethodB1` declare no constraint. They still report a parameter that carries `B2`'s
`struct` constraint. Generic types are affected as well as generic methods. This states the fault
without a `VerificationException`, without constraint satisfaction, and without the JIT.

The runs below use 150 iterations and two proxy types per class, so four emitted proxy types per
module. Each range covers three runs:

| mode | failures | mismatches |
| --- | --- | --- |
| serial (control) | 0/600 | 0/3600 |
| concurrent | 203-249/600 | 1404-1836/3600 |
| concurrent, prewarmed | 86-285/600 | 0-18/3600 |

```
./run-reflection-audit.sh          # ITER=150, COPIES=2 by default
```

The serial row is what gives the concurrent number meaning. It uses the same emit and the same
audit, and releases the threads one at a time. It is clean every time. It also checks the expected
names themselves.

`prewarmed` binds every generic parameter on one thread, by reflection, after emission and before
the concurrent window. It almost removes the mismatches. It leaves the invocation failures in the
same band. This first looked like two separate faults. It is one fault. Failures track the number of
sorts that reorder the table, and mismatches do not. Serial reorders nothing and throws nothing. See
"The exception, not just its wording" in [CORECLR-INVESTIGATION.md](CORECLR-INVESTIGATION.md).

Asking reflection for a parameter is what binds it. Nothing may read a parameter before the window,
unless that is the point of the run.

`dumptable` prints one module's GenericParam rows in rid order. The `Owner` column is a
`TypeOrMethodDef` coded token. It is not monotonic in rid order for this emit shape, in every module
sampled.

### The constraint is needed for the exception, not for the fault

This harness changes one row of the matrix above. `ONE_CLASS`, `ONE_TYPE`, and `OWN_GENERATOR` live
in MinimalEmitter's `Program.cs`, which this project does not compile. Only the `Inputs.cs` and
`Emitter.cs` conditions apply here:

| condition | failures | mismatches |
| --- | --- | --- |
| none, baseline | 209/600 | 1530/3600 |
| `args` not an array (`SCALAR`) | 0/600 | 0/3600 |
| constraint removed (`NO_CONSTRAINT`) | 0/600 | **1242/3600** |
| `MethodB1` deleted (`NO_SIBLING`) | 124/600 | 816/2700 |
| non-proceeding interceptor (`NO_PROCEED`) | 0/600 | 0/3600 |
| no invocation type (`NO_INVOCATION`) | 0/600 | 0/3600 |

Remove the constraint and the corruption stays, at close to the baseline rate. Nothing throws. An
unconstrained parameter has nothing to reject, so the wrong parameter goes unnoticed. Every other
condition zeroes the mismatches as well as the failures.

## IL verification

```
dotnet tool install -g dotnet-ilverify
ilverify emitted.dll \
  -r "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.11\*.dll" \
  -r "src\MinimalEmitter\bin\Debug\net10.0\*.dll" \
  -s System.Private.CoreLib
```

All three emitted images report `All Classes and Methods ... Verified`. The images come from two
capture routes:

- The two SRE repros use their own persisted mode. Pass a `.dll` path as an argument, for example
  `MinimalEmitter.exe 1 4 persisted emitted.dll`. This calls `PersistedAssemblyBuilder.Save`.
- The Castle repro uses `CastlePeCapture`. It hooks `PersistentProxyBuilder.AssemblyCreated` and
  writes `AssemblyBytes`. It needs a Castle.Core built from master, because the 5.2.1 package does
  not contain `PersistentProxyBuilder`.

`ilverify` reads a PE image only. All three results therefore describe the persisted rendering. The
image that actually fails is the one a runtime `AssemblyBuilder` holds in memory, and there is no
way to hand that image to `ilverify`. The emit calls are identical between the two modes. The
in-memory IL was also dumped through `GetILAsByteArray`, with metadata tokens resolved, and
compared. That is a weaker check than `ilverify`.

## Differences from the repro in the issue

`CastleHarness` does not require xunit. Three things changed:

1. A fresh `ProxyGenerator` per iteration. DynamicProxy caches proxy types per class. A shared
   generator would reuse cached types from the second iteration onward, and the loop would stop
   exercising generation.
2. Two threads released from a barrier, instead of two xunit test classes.
3. Conditions behind `#if`, so one build is one condition.

The `castle` column's rate is therefore not comparable to the original repro. Two things do compare:
whether a condition fails at all, and which generic parameter the runtime names.

`src/CastleXunitHarness` is the original repro. Only the `#if` variants differ, so the two shapes
compare directly:

| condition | xunit, original shape | console harness |
| --- | --- | --- |
| none, baseline | 20/20 runs, `B1` | 41/1200, `B1` |
| `MethodB1` deleted (`NO_SIBLING`) | 7/20 runs, `A2` | 35/1200, `A2` |
| the other six | 0/20 | 0 |

```
./run-xunit-comparison.sh          # RUNS=20 by default, one dotnet test run per trial
```

Both agree on every condition, and on the type parameter that the exception names. The
restructuring changes only how often the fault appears.

## Environment

Measured on .NET 10.0.11, Windows 11, 16 cores. `CastleHarness` uses Castle.Core 5.2.1.
`CastlePeCapture` uses Castle.Core master, built for `net9.0`.

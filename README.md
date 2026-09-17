# VerificationException from dynamically emitted generic methods

Investigation material for [castleproject/Core#648](https://github.com/castleproject/Core/issues/648).

Under concurrency, a generic method on a dynamically emitted type can fail verification with a type
argument that was never passed:

```
System.Security.VerificationException: Method B.MethodB2: type argument 'B1'
    violates the constraint of type parameter 'B2'.
```

The call site passes `int`. `B1` is the type parameter of a *different* generic method. The runtime
resolves the type argument to a parameter belonging to some other generic method in the same dynamic
module.

## Summary

- The fault seems to be related to the builder.  `PersistedAssemblyBuilder` never reproduces it; a runtime `AssemblyBuilder` does.
- A `System.Reflection.Emit` reproduction, with no Castle, shows the issue.
- All three harnesses emit IL accepted by `ilverify` (though only in its persisted rendering. See the
  details in the IL verification section).
- Generation does not have to overlap with JIT. Building every type first and then invoking
  concurrently reproduces the fault.

The `MinimalEmitter` in this repo puts N proxy types into **one** assembly, finishes emitting, then invokes them all
concurrently. Only the builder differs between the rows.

Every count below is failed invocations out of attempts:

| builder | 2 proxy types | 4 | 8 |
| --- | --- | --- | --- |
| `AssemblyBuilder` | 13/600 | 270/1200 | 1286/2400 |
| `PersistedAssemblyBuilder` | 0/600 | 0/1200 | 0/2400 |

```
./run-builder-comparison.sh            # ITER=300 by default
EMITTER=FullEmitter ./run-builder-comparison.sh
```

The persisted path never fails, at any proxy count. The `FullEmitter` reproduction gives the same split.

## Repository contents

| project | role |
| --- | --- |
| `src/MinimalEmitter` | reduced SRE emitter; runs the builder comparison and the `minimal` column |
| `src/FullEmitter` | tpflueger's emitter unreduced, same driver; the `full` column |
| `src/CastleHarness` | DynamicProxy from a console application; the `castle` column |
| `src/CastleXunitHarness` | the issue's repro in its original xunit shape, for the comparison above |
| `src/CastlePeCapture` | writes DynamicProxy's PE image for `ilverify` |

Both emitter projects share `Program.cs`, the runtime-versus-persisted driver. They differ only in
`Emitter.cs` and in the invocation base class their emitted types derive from.


## Comparing 3 reproductions in this repository

Three reproductions, the `A` and `B` classes from the issue, one condition changed per row, two proxy types
per module.

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

Conditions are compile-time, one per build: `dotnet build -p:Variant=NO_SIBLING`.

```
./run-condition-matrix.sh
ITER=200 VARIANTS="BASELINE NO_SIBLING" ./run-condition-matrix.sh
```

Every row agrees. The conditions are the five listed in the issue, plus the "only one type proxied"
observation from an earlier comment. Deleting `MethodB1` is the one addition here.

## IL verification

```
dotnet tool install -g dotnet-ilverify
ilverify emitted.dll \
  -r "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.11\*.dll" \
  -r "src\MinimalEmitter\bin\Debug\net10.0\*.dll" \
  -s System.Private.CoreLib
```

All three emitted images report `All Classes and Methods ... Verified`. The images come from two
different capture routes:

- For the two SRE repros: their own persisted mode. Pass a `.dll` path as an argument, for example
  `MinimalEmitter.exe 1 4 persisted emitted.dll`, which calls `PersistedAssemblyBuilder.Save`.
- For the Castle repro: `CastlePeCapture`, which hooks `PersistentProxyBuilder.AssemblyCreated` and writes
  `AssemblyBytes`. It needs a Castle.Core built from master, because `PersistentProxyBuilder` is not in
  the 5.2.1 package.

Note that `ilverify` can only read a PE image, so all three results describe
the persisted rendering of the output. I could not figure out how to take the image that actually fails, the one a runtime
`AssemblyBuilder` holds in memory, and send it to `ilverify`. The emit calls are identical
between the two modes, and the in-memory IL was separately dumped through `GetILAsByteArray` with
metadata tokens resolved and compared, but that is a different check than ilverify.

## Differences from the repro in the issue

`CastleHarness` is tweaked to not require xunit. Three things changed:

1. A fresh `ProxyGenerator` per iteration.
   DynamicProxy caches proxy types per class, so a shared generator would reuse cached types from the
   second iteration onward and the loop would stop exercising generation.
2. Two threads released from a barrier, instead of two xunit test classes.
4. Conditions behind `#if` so one build is one condition.

Because of this the `castle` column's *rate* is not comparable to the original repro. What compares is
whether a condition fails at all, and which generic parameter the runtime names.

`src/CastleXunitHarness` is the original repro, unchanged apart from the `#if` variants, so the two
shapes can be compared directly:

| condition | xunit, original shape | console harness |
| --- | --- | --- |
| none, baseline | 20/20 runs, `B1` | 41/1200, `B1` |
| `MethodB1` deleted (`NO_SIBLING`) | 7/20 runs, `A2` | 35/1200, `A2` |
| the other six | 0/20 | 0 |

```
./run-xunit-comparison.sh          # RUNS=20 by default, one dotnet test run per trial
```

Both agree on every condition and on the type parameter in the exception message. The restructuring just changes
how often the fault appears.

## Environment

Measured on .NET 10.0.11, Windows 11, 16 cores. `CastleHarness` uses Castle.Core 5.2.1;
`CastlePeCapture` uses Castle.Core master built for `net9.0`.

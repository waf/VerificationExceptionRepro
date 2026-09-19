// Concurrent type loads in one dynamic module corrupt the emitted generic parameters. Reflection
// then reports a parameter under another member's name, carrying another member's constraints: a
// method declaring no constraint comes back carrying `struct`. Generic types are hit too.
//
// Each run prints the invocation failures the runtime threw, and the parameters that came back
// under the wrong name.
//
//   ReflectionAudit [iterations] [copies] [serial] [prewarm] [auditall] [dumptable]
//
//     serial     invoke one thread at a time, instead of releasing them from a barrier
//     prewarm    bind every parameter on one thread before the concurrent window
//     auditall   audit every iteration, not only the ones that threw. serial needs this
//     dumptable  print the GenericParam rows of one module, in rid order
//
// This project compiles MinimalEmitter's Emitter.cs and Inputs.cs, so it runs the same emit
// sequence. The emitter copies each parameter name from its template, so the correct name is known
// in advance: MethodA1's parameter can only be A1. `serial` is the control, and it is clean every
// time.
//
// Cause, measured on a checked CoreCLR build. CORECLR-INVESTIGATION.md holds the evidence. Every
// type load in a dynamic module calls ReflectionModule::CaptureModuleMetaDataToMemory. It reaches
// that through NotifyDebuggerLoad, but it still runs with no debugger attached. The capture saves the
// scope, and saving sorts the GenericParam table and renumbers its rids. Nothing invalidates the
// rids the runtime already cached, so a TypeVarTypeDesc reads its name and its constraints from
// another parameter's row. That is also what throws: the constraint check then enforces another
// parameter's constraints. PersistedAssemblyBuilder never runs the capture, so it is immune.
//
// Traps:
//
//   - Reading a generic parameter binds it. Nothing may read one before the concurrent window.
//     `prewarm` does it deliberately. The GenericParam table check uses throwaway modules.
//   - -p:Variant= reaches only Inputs.cs and Emitter.cs: SCALAR, NO_CONSTRAINT, NO_SIBLING,
//     NO_PROCEED, NO_INVOCATION. ONE_CLASS, ONE_TYPE, and OWN_GENERATOR live in MinimalEmitter's
//     Program.cs, which this project does not compile, so a zero from them means nothing.
//   - NO_CONSTRAINT silences the exception and fixes nothing. The mismatches stay.

using System.Reflection;
using System.Reflection.Emit;

int[] numbers = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
int iterations = numbers.Length > 0 ? numbers[0] : 300;
int copies     = numbers.Length > 1 ? numbers[1] : 2;

bool serial    = args.Contains("serial");
bool prewarm   = args.Contains("prewarm");
bool auditAll  = args.Contains("auditall");
bool dumpTable = args.Contains("dumptable");

int failures = 0, attempts = 0, audited = 0, mismatches = 0;
int modulesChecked = 0, modulesNonMonotonic = 0, proxyTypes = 0;
var messages = new Dictionary<string, int>();
var findings = new Dictionary<string, int>();

// The structural check uses its own throwaway modules. It runs before the measurement loop, never
// inside it. Asking reflection for a generic parameter is what binds it. Running this check on a
// module that is about to be measured would prewarm that module and destroy the measurement.
int structureSample = Math.Min(iterations, 25);
for (int probe = 0; probe < structureSample; probe++)
{
    AssemblyBuilder throwaway = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName($"S{probe}"), AssemblyBuilderAccess.Run);
    ModuleBuilder throwawayModule = throwaway.DefineDynamicModule("<In Memory Module>");
    for (int copy = 0; copy < copies; copy++)
    {
        Emitter.EmitProxy(throwawayModule, typeof(A), $"s{copy}A");
        Emitter.EmitProxy(throwawayModule, typeof(B), $"s{copy}B");
    }

    CheckOwnerColumn(throwaway, dumpTable && probe == 0);
}

for (int iteration = 0; iteration < iterations; iteration++)
{
    AssemblyBuilder builder = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName($"R{iteration}"), AssemblyBuilderAccess.Run);
    ModuleBuilder module = builder.DefineDynamicModule("<In Memory Module>");

    var proxies = new List<(Type Proxy, bool Constrained)>();
    for (int copy = 0; copy < copies; copy++)
    {
        proxies.Add((Emitter.EmitProxy(module, typeof(A), $"s{copy}A").Proxy, false));
        proxies.Add((Emitter.EmitProxy(module, typeof(B), $"s{copy}B").Proxy, true));
    }

    proxyTypes = proxies.Count;

    // Binding every parameter here, on one thread, leaves the concurrent window nothing to bind.
    // It almost removes the mismatches, and leaves the invocation failures where they were. Both
    // come from the same fault. See "The exception, not just its wording" in
    // CORECLR-INVESTIGATION.md.
    if (prewarm)
    {
        foreach ((Type param, _, _) in GenericParams(builder)) _ = param.Name;
    }

    bool failed = false;
    var barrier = new Barrier(serial ? 1 : proxies.Count);
    var threads = new Thread[proxies.Count];
    for (int i = 0; i < proxies.Count; i++)
    {
        (Type proxyType, bool constrained) = proxies[i];
        threads[i] = new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                object instance = Activator.CreateInstance(proxyType);
#if SCALAR
                if (constrained) ((B)instance).MethodB2<int>(0);
                else ((A)instance).MethodA2<int>(0);
#else
                if (constrained) ((B)instance).MethodB2<int>(default);
                else ((A)instance).MethodA2<int>(default);
#endif
            }
            catch (Exception exception)
            {
                string key = $"{exception.GetType().Name}: {exception.Message}";
                lock (messages) messages[key] = messages.TryGetValue(key, out int n) ? n + 1 : 1;
                Interlocked.Increment(ref failures);
                Volatile.Write(ref failed, true);
            }
        });
    }

    if (serial)
    {
        foreach (Thread thread in threads) { thread.Start(); thread.Join(); }
    }
    else
    {
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();
    }

    attempts += proxies.Count;

    if (!failed && !auditAll) continue;

    foreach ((Type param, string expected, string owner) in GenericParams(builder))
    {
        audited++;
        if (param.Name == expected) continue;

        mismatches++;
        string key = $"{owner}: expected '{expected}' got '{param.Name}'"
                   + $" (attrs={param.GenericParameterAttributes},"
                   + $" tok=0x{param.MetadataToken:X8})";
        findings[key] = findings.TryGetValue(key, out int seen) ? seen + 1 : 1;
    }
}

Console.WriteLine("--- invocation failures ---");
if (messages.Count == 0) Console.WriteLine("      (none)");
foreach (KeyValuePair<string, int> pair in messages.OrderByDescending(p => p.Value).Take(4))
{
    Console.WriteLine($"{pair.Value,5}  {pair.Key}");
}

Console.WriteLine();
Console.WriteLine("--- generic parameters reflection reports under the wrong name ---");
if (findings.Count == 0) Console.WriteLine("      (none)");
foreach (KeyValuePair<string, int> pair in findings.OrderByDescending(p => p.Value).Take(10))
{
    Console.WriteLine($"{pair.Value,5}  {pair.Key}");
}

Console.WriteLine();
Console.WriteLine($"RESULT mode={(serial ? "serial" : "concurrent")} prewarm={prewarm}"
    + $" proxyTypes={proxyTypes} failures={failures}/{attempts}"
    + $" mismatches={mismatches}/{audited}"
    + $" nonMonotonicOwnerColumn={modulesNonMonotonic}/{modulesChecked}");
return mismatches > 0 ? 1 : 0;

// The emitter copies each template's parameter name onto the override, the callback, and the
// invocation type. The member name alone therefore gives the expected parameter name:
//   MethodA1, MethodA1_callback  ->  A1
//   s0A_MethodA1_<suffix>        ->  A1
IEnumerable<(Type Param, string Expected, string Owner)> GenericParams(Assembly emitted)
{
    foreach (Type type in emitted.GetTypes())
    {
        if (type.IsGenericTypeDefinition && type.Name.Contains("_Method", StringComparison.Ordinal))
        {
            string rest = type.Name[(type.Name.IndexOf("_Method", StringComparison.Ordinal) + 7)..];
            int underscore = rest.IndexOf('_');
            yield return (type.GetGenericArguments()[0],
                          underscore < 0 ? rest : rest[..underscore],
                          $"type {type.Name}");
        }

        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!method.IsGenericMethodDefinition ||
                !method.Name.StartsWith("Method", StringComparison.Ordinal))
            {
                continue;
            }

            string name = method.Name.EndsWith("_callback", StringComparison.Ordinal)
                ? method.Name[..^"_callback".Length]["Method".Length..]
                : method.Name["Method".Length..];

            yield return (method.GetGenericArguments()[0], name,
                          $"method {type.Name}.{method.Name}");
        }
    }
}

// The GenericParam table's Owner column is a TypeOrMethodDef coded token: rid << 1 | tag, with tag
// 0 for a TypeDef and 1 for a MethodDef. ECMA-335 requires the table to be sorted by Owner. This
// records whether the emitted module's table is sorted. For this emit shape, it is not.
void CheckOwnerColumn(Assembly emitted, bool dump)
{
    var rows = new List<(int Rid, long Owner, string Name)>();

    void Add(Type param, int ownerToken)
    {
        int ownerRid = ownerToken & 0x00FFFFFF;
        long tag = (ownerToken >> 24) == 0x06 ? 1 : 0;      // 0x06 MethodDef, 0x02 TypeDef
        rows.Add((param.MetadataToken & 0x00FFFFFF, ((long)ownerRid << 1) | tag, param.Name));
    }

    foreach (Type type in emitted.GetTypes())
    {
        if (type.IsGenericTypeDefinition)
        {
            foreach (Type param in type.GetGenericArguments()) Add(param, type.MetadataToken);
        }

        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!method.IsGenericMethodDefinition) continue;
            foreach (Type param in method.GetGenericArguments()) Add(param, method.MetadataToken);
        }
    }

    rows.Sort((x, y) => x.Rid.CompareTo(y.Rid));

    bool monotonic = true;
    for (int i = 1; i < rows.Count; i++)
    {
        if (rows[i].Owner < rows[i - 1].Owner) { monotonic = false; break; }
    }

    modulesChecked++;
    if (!monotonic) modulesNonMonotonic++;

    if (!dump) return;

    Console.WriteLine("--- GenericParam rows of the first module, in rid order ---");
    foreach ((int rid, long owner, string name) in rows)
    {
        Console.WriteLine($"  rid={rid,3}  codedOwner={owner,6}  {name}");
    }

    Console.WriteLine($"  owner column monotonic: {monotonic}");
    Console.WriteLine();
}

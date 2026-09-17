// Emit every proxy type first, then invoke them all concurrently. This is the shape the fault
// actually needs: generation does not have to overlap with JIT.
//
// Two builders are compared:
//   runtime   - a regular AssemblyBuilder, types runnable straight away
//   persisted - a PersistedAssemblyBuilder, saved to a stream and loaded back before use
// Emission is single-threaded in both, so the builder is the only difference.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

public static class Program
{
    private static int failures;
    private static int plannedTypes;

    public static int Main(string[] argv)
    {
        bool persisted = argv.Contains("persisted");
        int[] numbers = argv.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        int iterations = numbers.Length > 0 ? numbers[0] : 300;
        int copies = numbers.Length > 1 ? numbers[1] : 1;
        string savePath = argv.FirstOrDefault(a => a.EndsWith(".dll"));

        var messages = new Dictionary<string, int>();
        int attempts = 0;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var plan = new List<(string Name, bool Constrained)>();
            Type[] runnable = persisted
                ? EmitPersisted(iteration, copies, plan, savePath)
                : EmitRuntime(iteration, copies, plan);

            attempts += Invoke(runnable, plan, messages);
        }

        foreach (KeyValuePair<string, int> pair in messages.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"{pair.Value,5}  {pair.Key}");
        }

        string mode = persisted ? "persisted" : "runtime";
        Console.WriteLine(
            $"RESULT mode={mode} proxyTypes={plannedTypes} failures={failures}/{attempts}");
        return failures > 0 ? 1 : 0;
    }

    private static Type[] EmitRuntime(int iteration, int copies, List<(string, bool)> plan)
    {
        var created = new List<Type>();
        foreach (ModulePlan group in Groups(copies))
        {
            AssemblyBuilder builder = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"R{iteration}_{group.Tag}"), AssemblyBuilderAccess.Run);
            ModuleBuilder module = builder.DefineDynamicModule("<In Memory Module>");
            foreach ((Type baseType, bool constrained) in group.Items)
            {
                (Type proxy, string name) =
                    Emitter.EmitProxy(module, baseType, group.Tag + baseType.Name);
                created.Add(proxy);
                plan.Add((name, constrained));
            }
        }

        return created.ToArray();
    }

    private static Type[] EmitPersisted(
        int iteration, int copies, List<(string, bool)> plan, string savePath)
    {
        var images = new List<(PersistedAssemblyBuilder Builder, List<(string, bool)> Plan)>();
        foreach (ModulePlan group in Groups(copies))
        {
            var builder = new PersistedAssemblyBuilder(
                new AssemblyName($"P{iteration}_{group.Tag}"), typeof(object).Assembly);
            ModuleBuilder module = builder.DefineDynamicModule("<In Memory Module>");
            var groupPlan = new List<(string, bool)>();
            foreach ((Type baseType, bool constrained) in group.Items)
            {
                (Type _, string name) =
                    Emitter.EmitProxy(module, baseType, group.Tag + baseType.Name);
                groupPlan.Add((name, constrained));
            }

            images.Add((builder, groupPlan));
        }

        var loaded = new List<Type>();
        bool savedAlready = false;
        foreach ((PersistedAssemblyBuilder builder, List<(string, bool)> groupPlan) in images)
        {
            using var stream = new MemoryStream();
            builder.Save(stream);

            if (savePath is not null && iteration == 0 && !savedAlready)
            {
                File.WriteAllBytes(savePath, stream.ToArray());
                Console.WriteLine($"saved {savePath}");
                savedAlready = true;
            }

            stream.Position = 0;

            // The default context keeps A, B and InvBase identical to the ones already loaded,
            // so the casts in Invoke work.
            Assembly image = AssemblyLoadContext.Default.LoadFromStream(stream);
            foreach ((string name, bool constrained) in groupPlan)
            {
                loaded.Add(image.GetType(name, throwOnError: true));
                plan.Add((name, constrained));
            }
        }

        return loaded.ToArray();
    }

    private readonly record struct ModulePlan(string Tag, (Type Base, bool Constrained)[] Items);

    // Decides which proxy types share a module.
    private static IEnumerable<ModulePlan> Groups(int copies)
    {
        var items = new List<(Type, bool)>();
        for (int copy = 0; copy < copies; copy++)
        {
#if ONE_TYPE
            items.Add((typeof(B), true));
#else
            items.Add((typeof(A), false));
            items.Add((typeof(B), true));
#endif
        }

        plannedTypes = items.Count;

#if OWN_GENERATOR
        // A ProxyGenerator per type amounts to a module per type.
        for (int i = 0; i < items.Count; i++)
        {
            yield return new ModulePlan($"g{i}", [items[i]]);
        }
#else
        yield return new ModulePlan("s", items.ToArray());
#endif
    }

    private static int Invoke(
        Type[] runnable, List<(string Name, bool Constrained)> plan, Dictionary<string, int> messages)
    {
        void Call(Type proxyType, bool constrained)
        {
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
                lock (messages)
                {
                    messages[key] = messages.TryGetValue(key, out int n) ? n + 1 : 1;
                }

                Interlocked.Increment(ref failures);
            }
        }

#if ONE_CLASS
        // No concurrency: one thread calls everything in order.
        for (int i = 0; i < runnable.Length; i++)
        {
            Call(runnable[i], plan[i].Constrained);
        }
#else
        var barrier = new Barrier(runnable.Length);
        var threads = new Thread[runnable.Length];
        for (int i = 0; i < runnable.Length; i++)
        {
            Type proxyType = runnable[i];
            bool constrained = plan[i].Constrained;
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                Call(proxyType, constrained);
            });
        }

        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();
#endif
        return runnable.Length;
    }
}

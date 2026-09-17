// DynamicProxy in the same shape as the SRE harnesses: build every proxy first, then invoke them
// all concurrently. Run from a console application, so the numbers share units with the others.

using System.Collections.Concurrent;
using Castle.DynamicProxy;

public static class Program
{
    private static int failures;

    public static int Main(string[] argv)
    {
        int iterations = argv.Length > 0 ? int.Parse(argv[0]) : 600;
        var messages = new ConcurrentDictionary<string, int>();
        int attempts = 0;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            // A ProxyGenerator owns one dynamic module, so a fresh one per iteration gives a fresh
            // module. Under OWN_GENERATOR each input class gets its own.
            var shared = new ProxyGenerator();
#if OWN_GENERATOR
            ProxyGenerator forA = new();
            ProxyGenerator forB = new();
#else
            ProxyGenerator forA = shared;
            ProxyGenerator forB = shared;
#endif

            // Phase one: every proxy is created before anything is invoked.
            var targets = new List<Action>();
            try
            {
#if !ONE_TYPE
                A proxyA = forA.CreateClassProxy<A>(Interceptor());
                targets.Add(() => CallA(proxyA, messages));
#endif
                B proxyB = forB.CreateClassProxy<B>(Interceptor());
                targets.Add(() => CallB(proxyB, messages));
            }
            catch (Exception exception)
            {
                Record(exception, messages);
                continue;
            }

            // Phase two: invoke.
#if ONE_CLASS
            foreach (Action target in targets)
            {
                target();
            }
#else
            var barrier = new Barrier(targets.Count);
            var threads = new Thread[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                Action target = targets[i];
                threads[i] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    target();
                });
            }

            foreach (Thread thread in threads) thread.Start();
            foreach (Thread thread in threads) thread.Join();
#endif
            attempts += targets.Count;
        }

        foreach (KeyValuePair<string, int> pair in messages.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"{pair.Value,5}  {pair.Key}");
        }

        Console.WriteLine($"RESULT failures={failures}/{attempts}");
        return failures > 0 ? 1 : 0;
    }

    private static IInterceptor Interceptor() =>
#if NO_PROCEED
        new NonProceeding();
#else
        new StandardInterceptor();
#endif

    private static void CallA(A proxy, ConcurrentDictionary<string, int> messages)
    {
        try
        {
#if SCALAR
            proxy.MethodA2<int>(0);
#else
            proxy.MethodA2<int>(default!);
#endif
        }
        catch (Exception exception)
        {
            Record(exception, messages);
        }
    }

    private static void CallB(B proxy, ConcurrentDictionary<string, int> messages)
    {
        try
        {
#if SCALAR
            proxy.MethodB2<int>(0);
#else
            proxy.MethodB2<int>(default!);
#endif
        }
        catch (Exception exception)
        {
            Record(exception, messages);
        }
    }

    private static void Record(Exception exception, ConcurrentDictionary<string, int> messages)
    {
        messages.AddOrUpdate($"{exception.GetType().Name}: {exception.Message}", 1, (_, n) => n + 1);
        Interlocked.Increment(ref failures);
    }

    private sealed class NonProceeding : IInterceptor
    {
        public void Intercept(IInvocation invocation) { }
    }
}

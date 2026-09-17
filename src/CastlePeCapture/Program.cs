// Captures the PE image DynamicProxy produces, using master's PersistentProxyBuilder, so its IL
// can be run through ILVerify alongside the two SRE harnesses.

using Castle.DynamicProxy;

public class A
{
    public virtual void MethodA1<A1>() { }

    public virtual void MethodA2<A2>(A2[] args) { }
}

public class B
{
    public virtual void MethodB1<B1>() { }

    public virtual void MethodB2<B2>(B2[] args)
        where B2 : struct { }
}

public static class Program
{
    public static void Main()
    {
        var builder = new PersistentProxyBuilder();
        int saved = 0;
        builder.AssemblyCreated += (_, args) =>
        {
            string path = $"castle-proxy-{++saved}.dll";
            File.WriteAllBytes(path, args.AssemblyBytes);
            Console.WriteLine($"saved {path} ({args.AssemblyBytes.Length} bytes)");
        };

        var generator = new ProxyGenerator(builder);
        generator.CreateClassProxy<A>(new StandardInterceptor());
        generator.CreateClassProxy<B>(new StandardInterceptor());
        Console.WriteLine($"assemblies captured: {saved}");
    }
}

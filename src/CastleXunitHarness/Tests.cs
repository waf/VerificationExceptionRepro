using Castle.DynamicProxy;
using Xunit;

// Castle side of the black-box equivalence check for castleproject/Core#648.
// Exactly one condition changes per build, selected with -p:Variant=NAME.

public class A
{
    public virtual void MethodA1<A1>() { }

#if SCALAR
    public virtual void MethodA2<A2>(A2 args) { }
#else
    public virtual void MethodA2<A2>(A2[] args) { }
#endif
}

public class B
{
#if !NO_SIBLING
    public virtual void MethodB1<B1>() { }
#endif

#if SCALAR
    public virtual void MethodB2<B2>(B2 args)
        where B2 : struct { }
#elif NO_CONSTRAINT
    public virtual void MethodB2<B2>(B2[] args) { }
#else
    public virtual void MethodB2<B2>(B2[] args)
        where B2 : struct { }
#endif
}

public sealed class NonProceedingInterceptor : IInterceptor
{
    public void Intercept(IInvocation invocation) { }
}

public static class Gen
{
#if OWN_GENERATOR
    public static ProxyGenerator ForA { get; } = new();

    public static ProxyGenerator ForB { get; } = new();
#else
    private static readonly ProxyGenerator Shared = new();

    public static ProxyGenerator ForA => Shared;

    public static ProxyGenerator ForB => Shared;
#endif

    public static IInterceptor Interceptor() =>
#if NO_PROCEED
        new NonProceedingInterceptor();
#else
        new StandardInterceptor();
#endif
}

#if ONE_CLASS
public class TestBoth
{
    private readonly A a = Gen.ForA.CreateClassProxy<A>(Gen.Interceptor());
    private readonly B b = Gen.ForB.CreateClassProxy<B>(Gen.Interceptor());

    [Fact]
    public void TestA()
    {
#if SCALAR
        a.MethodA2<int>(0);
#else
        a.MethodA2<int>(default!);
#endif
    }

    [Fact]
    public void TestB()
    {
#if SCALAR
        b.MethodB2<int>(0);
#else
        b.MethodB2<int>(default!);
#endif
    }
}
#else
#if !ONE_TYPE
public class TestA
{
    private readonly A a = Gen.ForA.CreateClassProxy<A>(Gen.Interceptor());

    [Fact]
    public void Test()
    {
#if SCALAR
        a.MethodA2<int>(0);
#else
        a.MethodA2<int>(default!);
#endif
    }
}
#endif

public class TestB
{
    private readonly B b = Gen.ForB.CreateClassProxy<B>(Gen.Interceptor());

    [Fact]
    public void Test()
    {
#if SCALAR
        b.MethodB2<int>(0);
#else
        b.MethodB2<int>(default!);
#endif
    }
}
#endif

// The input classes from castleproject/Core#648. All three harnesses compile the same ones, so
// every column of the condition matrix is driven by identical inputs. One condition changes per
// build, selected with -p:Variant=NAME.
//
// The invocation base keeps the five-argument shape of the unreduced emitter, mirroring Castle's
// AbstractInvocation.

using System;
using System.Reflection;

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

public abstract class BmInvocationBase
{
    public Type TargetType;
    public object Proxy;
    public object[] Interceptors;
    public MethodInfo Method;
    public object[] Arguments;
    public Type[] GenericArgs;

    protected BmInvocationBase(
        Type targetType, object proxy, object[] interceptors, MethodInfo method, object[] arguments)
    {
        TargetType = targetType;
        Proxy = proxy;
        Interceptors = interceptors;
        Method = method;
        Arguments = arguments;
    }

    public void SetGenericMethodArguments(Type[] args) => GenericArgs = args;

    public abstract void InvokeMethodOnTarget();

    public void Proceed() => InvokeMethodOnTarget();
}

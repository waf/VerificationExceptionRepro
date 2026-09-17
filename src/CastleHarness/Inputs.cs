// The input classes from castleproject/Core#648. All three harnesses compile the same ones, so
// every column of the condition matrix is driven by identical inputs. One condition changes per
// build, selected with -p:Variant=NAME.

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

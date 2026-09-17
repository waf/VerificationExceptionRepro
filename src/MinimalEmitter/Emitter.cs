using System.Reflection;
using System.Reflection.Emit;

// The reduced emitter, unchanged apart from returning the emitted type name so the
// persisted mode can find the type again after loading.
public static class Emitter
{
    public static (Type Proxy, string Name) EmitProxy(ModuleBuilder module, Type baseType, string tag)
    {
        string suffix = Guid.NewGuid().ToString("N");
        TypeBuilder proxy = module.DefineType(
            $"Castle.Proxies.{tag}Proxy{suffix}", TypeAttributes.Public | TypeAttributes.Class, baseType);

        // Every virtual generic method gets overridden, exactly as DynamicProxy does. The unused
        // sibling methods matter: they put more generic methods in the module.
        MethodInfo[] templates = baseType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.IsVirtual && m.IsGenericMethodDefinition && m.DeclaringType == baseType)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (MethodInfo template in templates)
        {
            ParameterInfo[] parameters = template.GetParameters();

            MethodBuilder callback = DefineGenericMethod(proxy, template.Name + "_callback", template,
                (il, gp) =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    if (parameters.Length == 1) il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, template.MakeGenericMethod(gp[0]));
                    il.Emit(OpCodes.Ret);
                });

            (TypeBuilder invType, ConstructorBuilder invCtor) = EmitInvocationType(
                module, $"Castle.Proxies.Invocations.{tag}_{template.Name}_{suffix}",
                proxy, callback, template);

            MethodBuilder over = DefineGenericMethod(proxy, template.Name, template, (il, gp) =>
            {
                Type closedInv = invType.MakeGenericType(gp[0]);
                ConstructorInfo closedCtor = TypeBuilder.GetConstructor(closedInv, invCtor);
                LocalBuilder arguments = il.DeclareLocal(typeof(object[]));

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, parameters.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                il.Emit(OpCodes.Stloc, arguments);

                if (parameters.Length == 1)
                {
                    il.Emit(OpCodes.Ldloc, arguments);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ldarg_1);
                    if (!parameters[0].ParameterType.IsArray)
                    {
                        il.Emit(OpCodes.Box, gp[0]);
                    }

                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Ldloc, arguments);
                il.Emit(OpCodes.Newobj, closedCtor);
#if NO_PROCEED
                // Mirrors a non-proceeding interceptor: the invocation is built but never run.
                il.Emit(OpCodes.Pop);
#else
                il.Emit(OpCodes.Call, typeof(InvBase).GetMethod(nameof(InvBase.Proceed))!);
#endif
                il.Emit(OpCodes.Ret);
            });

            proxy.DefineMethodOverride(over, template);
        }

        return (proxy.CreateType(), proxy.FullName);
    }

    private static MethodBuilder DefineGenericMethod(
        TypeBuilder type,
        string name,
        MethodInfo template,
        Action<ILGenerator, GenericTypeParameterBuilder[]> emitBody)
    {
        MethodBuilder method = type.DefineMethod(
            name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig);

        Type original = template.GetGenericArguments()[0];
        GenericTypeParameterBuilder[] gp = method.DefineGenericParameters(original.Name);
        CopyConstraints(gp[0], original);

        ParameterInfo[] parameters = template.GetParameters();
        Type[] signature = parameters.Length == 1
            ? [ParameterType(parameters[0], gp[0])]
            : Type.EmptyTypes;
        method.SetParameters(signature);
        method.SetReturnType(typeof(void));
        emitBody(method.GetILGenerator(), gp);
        return method;
    }

    private static Type ParameterType(ParameterInfo parameter, GenericTypeParameterBuilder gp) =>
        parameter.ParameterType.IsArray ? gp.MakeArrayType() : gp;

    // Mirrors DynamicProxy's GenericUtil: copy the attributes, then the constraints.
    private static void CopyConstraints(GenericTypeParameterBuilder target, Type original)
    {
        target.SetGenericParameterAttributes(original.GenericParameterAttributes);
        Type[] constraints = original.GetGenericParameterConstraints();
        if (constraints.Length > 0)
        {
            target.SetInterfaceConstraints(constraints);
        }
    }

    private static (TypeBuilder Type, ConstructorBuilder Ctor) EmitInvocationType(
        ModuleBuilder module,
        string name,
        TypeBuilder proxy,
        MethodBuilder callback,
        MethodInfo template)
    {
        TypeBuilder inv = module.DefineType(
            name, TypeAttributes.Public | TypeAttributes.Class, typeof(InvBase));

        Type original = template.GetGenericArguments()[0];
        GenericTypeParameterBuilder[] gp = inv.DefineGenericParameters(original.Name);
        CopyConstraints(gp[0], original);

        Type[] ctorArgs = [typeof(object), typeof(object[])];
        ConstructorBuilder ctor = inv.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, ctorArgs);
        ILGenerator cil = ctor.GetILGenerator();
        cil.Emit(OpCodes.Ldarg_0);
        for (int i = 1; i <= ctorArgs.Length; i++) cil.Emit(OpCodes.Ldarg, i);
        cil.Emit(OpCodes.Call, typeof(InvBase).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)[0]);
        cil.Emit(OpCodes.Ret);

        MethodBuilder invoke = inv.DefineMethod(
            "InvokeMethodOnTarget",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void),
            Type.EmptyTypes);

        ParameterInfo[] parameters = template.GetParameters();
        ILGenerator il = invoke.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, typeof(InvBase).GetField(nameof(InvBase.Proxy))!);
        il.Emit(OpCodes.Isinst, proxy);

        if (parameters.Length == 1)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, typeof(InvBase).GetField(nameof(InvBase.Arguments))!);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem_Ref);
            if (parameters[0].ParameterType.IsArray)
            {
                il.Emit(OpCodes.Castclass, gp[0].MakeArrayType());
            }
            else
            {
                il.Emit(OpCodes.Unbox_Any, gp[0]);
            }
        }

        il.Emit(OpCodes.Callvirt, callback.MakeGenericMethod(gp[0]));
        il.Emit(OpCodes.Ret);
        inv.DefineMethodOverride(invoke, typeof(InvBase).GetMethod(nameof(InvBase.InvokeMethodOnTarget))!);

        inv.CreateType();
        return (inv, ctor);
    }
}

using System.Reflection;
using System.Reflection.Emit;

// The unreduced emitter, returning the emitted type name so the persisted mode can
// find the type again after loading.
public static class Emitter
{
    public static (Type Proxy, string Name) EmitProxy(ModuleBuilder module, Type baseType, string tag)
    {
        string suffix = Guid.NewGuid().ToString("N");

        TypeBuilder proxy = module.DefineType(
            $"Castle.Proxies.{tag}Proxy{suffix}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Serializable,
            baseType);
        proxy.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(SerializableAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));
        FieldBuilder interceptorsField = proxy.DefineField(
            "__interceptors", typeof(object[]), FieldAttributes.Private);
        proxy.DefineField(
            "proxyGenerationOptions", typeof(object), FieldAttributes.Private | FieldAttributes.Static);

        MethodInfo[] templates = baseType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.IsVirtual && m.IsGenericMethodDefinition && m.DeclaringType == baseType)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (MethodInfo template in templates)
        {
            ParameterInfo[] parameters = template.GetParameters();

            MethodBuilder callback = EmitFromTemplate(proxy, template, template.Name + "_callback",
                (il, gp) =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    if (parameters.Length == 1) il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, template.MakeGenericMethod(gp[0]));
                    il.Emit(OpCodes.Ret);
                });

            (TypeBuilder invType, ConstructorBuilder invCtor) = EmitInvocationType(
                module, $"{tag}_{template.Name}_{suffix}", proxy, callback, template);

            EmitFromTemplate(proxy, template, template.Name, (il, gp) =>
            {
                Type closedInv = invType.MakeGenericType(gp[0]);
                ConstructorInfo closedCtor = TypeBuilder.GetConstructor(closedInv, invCtor);

                LocalBuilder invocation = il.DeclareLocal(typeof(BmInvocationBase));
                LocalBuilder typeArgs = il.DeclareLocal(typeof(Type[]));
                LocalBuilder argArray = il.DeclareLocal(typeof(object[]));

                MethodInfo getTypeFromHandle = typeof(Type).GetMethod(
                    nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!;
                MethodInfo getMethodFromHandle = typeof(MethodBase).GetMethod(
                    nameof(MethodBase.GetMethodFromHandle),
                    [typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle)])!;

                // arg1: Type targetType
                il.Emit(OpCodes.Ldtoken, baseType);
                il.Emit(OpCodes.Call, getTypeFromHandle);
                // arg2: object proxy
                il.Emit(OpCodes.Ldarg_0);
                // arg3: object[] interceptors
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, interceptorsField);
                // arg4: MethodInfo method, resolved from the MethodSpec token at runtime
                il.Emit(OpCodes.Ldtoken, template.MakeGenericMethod(gp[0]));
                il.Emit(OpCodes.Ldtoken, baseType);
                il.Emit(OpCodes.Call, getMethodFromHandle);
                il.Emit(OpCodes.Castclass, typeof(MethodInfo));
                // arg5: object[] arguments
                il.Emit(OpCodes.Ldc_I4, parameters.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                il.Emit(OpCodes.Stloc, argArray);
                if (parameters.Length == 1)
                {
                    il.Emit(OpCodes.Ldloc, argArray);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ldarg_1);
                    if (!parameters[0].ParameterType.IsArray) il.Emit(OpCodes.Box, gp[0]);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Ldloc, argArray);

                il.Emit(OpCodes.Newobj, closedCtor);
                il.Emit(OpCodes.Stloc, invocation);

                // Type[] { typeof(!!0) } materialises the method's own generic parameter.
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Newarr, typeof(Type));
                il.Emit(OpCodes.Stloc, typeArgs);
                il.Emit(OpCodes.Ldloc, typeArgs);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldtoken, gp[0]);
                il.Emit(OpCodes.Call, getTypeFromHandle);
                il.Emit(OpCodes.Stelem_Ref);
                il.Emit(OpCodes.Ldloc, invocation);
                il.Emit(OpCodes.Ldloc, typeArgs);
                il.Emit(OpCodes.Call, typeof(BmInvocationBase).GetMethod(
                    nameof(BmInvocationBase.SetGenericMethodArguments))!);

#if NO_PROCEED
                // Mirrors a non-proceeding interceptor.
#else
                il.Emit(OpCodes.Ldloc, invocation);
                il.Emit(OpCodes.Call, typeof(BmInvocationBase).GetMethod(
                    nameof(BmInvocationBase.Proceed))!);
#endif
                il.Emit(OpCodes.Ret);
            }, isOverride: true);
        }

        proxy.DefineTypeInitializer().GetILGenerator().Emit(OpCodes.Ret);
        return (proxy.CreateType(), proxy.FullName);
    }

    private static MethodBuilder EmitFromTemplate(
        TypeBuilder proxy,
        MethodInfo template,
        string name,
        Action<ILGenerator, GenericTypeParameterBuilder[]> emitBody,
        bool isOverride = false)
    {
        MethodBuilder mb = proxy.DefineMethod(
            name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig);

        Type[] originals = template.GetGenericArguments();
        GenericTypeParameterBuilder[] gp = mb.DefineGenericParameters(
            Array.ConvertAll(originals, t => t.Name));
        for (int i = 0; i < gp.Length; i++)
        {
            try
            {
                gp[i].SetGenericParameterAttributes(originals[i].GenericParameterAttributes);
                gp[i].SetInterfaceConstraints(originals[i].GetGenericParameterConstraints());
            }
            catch (NotSupportedException)
            {
                gp[i].SetGenericParameterAttributes(GenericParameterAttributes.None);
            }
        }

        ParameterInfo[] baseParams = template.GetParameters();
        Type[] parameterTypes = Array.ConvertAll(baseParams, p => p.ParameterType);
        Type returnType = template.ReturnType;

        mb.SetParameters(parameterTypes);
        mb.SetReturnType(returnType);

        Type[] retReq = template.ReturnParameter.GetRequiredCustomModifiers();
        Array.Reverse(retReq);
        Type[] retOpt = template.ReturnParameter.GetOptionalCustomModifiers();
        Array.Reverse(retOpt);
        var paramReq = new Type[baseParams.Length][];
        var paramOpt = new Type[baseParams.Length][];
        for (int i = 0; i < baseParams.Length; i++)
        {
            paramReq[i] = baseParams[i].GetRequiredCustomModifiers();
            Array.Reverse(paramReq[i]);
            paramOpt[i] = baseParams[i].GetOptionalCustomModifiers();
            Array.Reverse(paramOpt[i]);
        }

        mb.SetSignature(returnType, retReq, retOpt, parameterTypes, paramReq, paramOpt);

        for (int i = 0; i < baseParams.Length; i++)
        {
            mb.DefineParameter(i + 1, baseParams[i].Attributes, baseParams[i].Name);
        }

        emitBody(mb.GetILGenerator(), gp);

        if (isOverride) proxy.DefineMethodOverride(mb, template);
        return mb;
    }

    private static (TypeBuilder Type, ConstructorBuilder Ctor) EmitInvocationType(
        ModuleBuilder module,
        string name,
        TypeBuilder proxy,
        MethodBuilder callback,
        MethodInfo template)
    {
        Type original = template.GetGenericArguments()[0];
        TypeBuilder inv = module.DefineType(
            $"Castle.Proxies.Invocations.{name}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Serializable,
            typeof(BmInvocationBase));
        inv.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(SerializableAttribute).GetConstructor(Type.EmptyTypes)!, Array.Empty<object>()));

        GenericTypeParameterBuilder[] gp = inv.DefineGenericParameters(original.Name);
        try
        {
            gp[0].SetGenericParameterAttributes(original.GenericParameterAttributes);
            gp[0].SetInterfaceConstraints(original.GetGenericParameterConstraints());
        }
        catch (NotSupportedException)
        {
        }

        ConstructorInfo baseCtor = typeof(BmInvocationBase).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)[0];
        Type[] ctorArgs =
        [
            typeof(Type), typeof(object), typeof(object[]), typeof(MethodInfo), typeof(object[]),
        ];
        ConstructorBuilder ctor = inv.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, ctorArgs);
        ILGenerator cil = ctor.GetILGenerator();
        cil.Emit(OpCodes.Ldarg_0);
        for (int i = 1; i <= ctorArgs.Length; i++) cil.Emit(OpCodes.Ldarg, i);
        cil.Emit(OpCodes.Call, baseCtor);
        cil.Emit(OpCodes.Ret);

        MethodBuilder invoke = inv.DefineMethod(
            "InvokeMethodOnTarget",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void),
            Type.EmptyTypes);

        ParameterInfo[] parameters = template.GetParameters();
        ILGenerator il = invoke.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, typeof(BmInvocationBase).GetField(nameof(BmInvocationBase.Proxy))!);
        il.Emit(OpCodes.Isinst, proxy);
        if (parameters.Length == 1)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, typeof(BmInvocationBase).GetField(
                nameof(BmInvocationBase.Arguments))!);
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
        inv.DefineMethodOverride(
            invoke, typeof(BmInvocationBase).GetMethod(nameof(BmInvocationBase.InvokeMethodOnTarget))!);

        inv.CreateType();
        return (inv, ctor);
    }
}

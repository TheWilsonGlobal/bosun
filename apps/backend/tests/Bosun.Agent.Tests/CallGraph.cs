using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Bosun.Agent.Tests;

/// <summary>
/// A small IL call-graph reader for the safety-invariant tests. It sees direct calls, delegate creations (lambdas), constructors and the
/// bodies of async methods (their compiler-generated state machines), and resolves interface and virtual calls to the implementations
/// that exist in Bosun assemblies. It cannot see reflection or calls through delegates stored elsewhere: the tests assert that it DOES
/// see known calls, so a blind scanner fails instead of passing.
/// </summary>
public static class CallGraph
{
    private static readonly Dictionary<short, OpCode> OpCodeTable = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private const BindingFlags AllDeclared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Every Bosun assembly that is loaded, after forcing the references of the given ones to load.</summary>
    public static IReadOnlyList<Assembly> BosunAssemblies(params Assembly[] roots)
    {
        var seen = new Dictionary<string, Assembly>();

        void Visit(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? "";
            if (!name.StartsWith("Bosun", StringComparison.Ordinal) || name.EndsWith(".Tests", StringComparison.Ordinal) || name.EndsWith(".IntegrationTests", StringComparison.Ordinal) || !seen.TryAdd(name, assembly))
            {
                return;
            }

            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => r.Name?.StartsWith("Bosun", StringComparison.Ordinal) == true))
            {
                try
                {
                    Visit(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // not part of this test's dependency closure
                }
            }
        }

        foreach (var root in roots)
        {
            Visit(root);
        }

        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            Visit(loaded);
        }

        return [.. seen.Values];
    }

    public static bool IsBosun(MethodBase method) => method.DeclaringType?.Assembly.GetName().Name?.StartsWith("Bosun", StringComparison.Ordinal) == true;

    /// <summary>The method and, for an async method, the MoveNext of its state machine (where the code really is).</summary>
    public static IEnumerable<MethodBase> Bodies(MethodBase method)
    {
        yield return method;
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { StateMachineType: { } machine })
        {
            foreach (var move in machine.GetMethods(AllDeclared).Where(m => m.Name == "MoveNext"))
            {
                yield return move;
            }
        }
    }

    /// <summary>Methods called, created as delegates or constructed directly by the body of <paramref name="method"/> (state machine included).</summary>
    public static IEnumerable<MethodBase> Callees(MethodBase method) => Bodies(method).SelectMany(DirectCallees);

    /// <summary>Every method and constructor declared by a type and its nested types (closures, state machines).</summary>
    public static IEnumerable<MethodBase> AllMethods(Type type)
    {
        foreach (var method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
        {
            yield return method;
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var method in AllMethods(nested))
            {
                yield return method;
            }
        }
    }

    /// <summary>
    /// Everything reachable from the roots within <paramref name="depth"/> call levels (1 = the roots' own callees). Only Bosun methods are
    /// expanded; framework methods are returned as leaves. Interface and abstract calls are expanded into their Bosun implementations.
    /// </summary>
    public static HashSet<MethodBase> Reachable(IEnumerable<MethodBase> roots, int depth, IReadOnlyList<Assembly> assemblies)
    {
        var all = new HashSet<MethodBase>();
        var frontier = roots.SelectMany(AllBodiesOf).ToList();
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = new List<MethodBase>();
            foreach (var method in frontier)
            {
                foreach (var callee in Callees(method))
                {
                    var targets = Implementations(callee, assemblies).Prepend(callee).Distinct();
                    foreach (var target in targets)
                    {
                        if (all.Add(target) && IsBosun(target))
                        {
                            next.Add(target);
                        }
                    }
                }
            }

            frontier = next;
        }

        return all;
    }

    private static IEnumerable<MethodBase> AllBodiesOf(MethodBase method) => Bodies(method);

    /// <summary>The Bosun methods that implement an interface member or override a virtual one.</summary>
    public static IEnumerable<MethodBase> Implementations(MethodBase callee, IReadOnlyList<Assembly> assemblies)
    {
        if (callee is not MethodInfo info || info.DeclaringType is not { } declaring || !(declaring.IsInterface || info.IsAbstract || info.IsVirtual))
        {
            yield break;
        }

        foreach (var type in assemblies.SelectMany(SafeTypes).Where(t => !t.IsAbstract && !t.IsInterface))
        {
            if (declaring.IsInterface)
            {
                if (!declaring.IsAssignableFrom(type))
                {
                    continue;
                }

                var map = type.GetInterfaceMap(declaring);
                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i] == info)
                    {
                        yield return map.TargetMethods[i];
                    }
                }
            }
            else if (type.IsSubclassOf(declaring))
            {
                var over = type.GetMethod(info.Name, AllDeclared, null, info.GetParameters().Select(p => p.ParameterType).ToArray(), null);
                if (over is not null)
                {
                    yield return over;
                }
            }
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<MethodBase> DirectCallees(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        var module = method.Module;
        var i = 0;
        while (i < il.Length)
        {
            short code = il[i++];
            if (code == 0xFE)
            {
                code = (short)(0xFE00 | il[i++]);
            }

            var op = OpCodeTable[code];
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, i) * 4),
                _ => 4,
            };

            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? callee = null;
                try
                {
                    callee = module.ResolveMethod(BitConverter.ToInt32(il, i), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or BadImageFormatException)
                {
                    // a token that cannot be resolved in this context is skipped; the tests prove the scan sees known calls
                }

                if (callee is not null)
                {
                    yield return callee;
                }
            }

            i += size;
        }
    }
}

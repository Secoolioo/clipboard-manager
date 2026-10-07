using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace ClipboardManager.Tests.Privacy;

/// <summary>
/// "No network unless the user checks for updates" as a checked property: Core references no
/// networking assembly at all, the app uses System.Net.* only in the updater namespace (found by
/// scanning signatures and every method body), and native code is called only in an allow-list of
/// local Windows libraries.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class NoNetworkTests
{
    private const string UpdaterNamespace = "ClipboardManager.Updates";
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Assembly CoreAssembly = typeof(Core.AppPaths).Assembly;
    private static readonly Assembly AppAssembly = typeof(ClipboardManager.Shell.HostWindow).Assembly;
    private static readonly Assembly[] Ours = [CoreAssembly, AppAssembly];

    private static readonly HashSet<string> AllowedNativeLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "user32.dll", "kernel32.dll", "shell32.dll", "shcore.dll", "dwmapi.dll", "wtsapi32.dll", "psapi.dll",
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .DistinctBy(o => o.Value)
        .ToDictionary(o => o.Value);

    [Fact]
    public void Core_references_no_networking_assembly()
    {
        var networking = CoreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(networking.Count == 0, $"Core references {string.Join(", ", networking)}");
    }

    [Fact]
    public void Networking_is_used_only_by_the_updater()
    {
        var offenders = AppAssembly.GetTypes()
            .Where(t => t.Namespace != UpdaterNamespace)
            .SelectMany(t => NetworkingUses(t).Select(use => $"{t.FullName}: {use}"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_scan_sees_the_updaters_networking()
    {
        // Guards the test above against a scanner that silently finds nothing.
        var updater = typeof(ClipboardManager.Updates.UpdateService);
        var uses = new[] { updater }.Concat(updater.GetNestedTypes(Declared)).SelectMany(NetworkingUses).ToList();
        Assert.Contains(uses, u => u.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.Contains(uses, u => u.Contains("SendAsync", StringComparison.Ordinal));
        Assert.Contains(uses, u => u.Contains("System.Net.Http.SocketsHttpHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_calls_only_target_local_windows_libraries()
    {
        foreach (var assembly in Ours)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<DllImportAttribute>() is { } import)
                    {
                        Assert.True(AllowedNativeLibraries.Contains(import.Value), $"{type.FullName}.{method.Name} imports {import.Value}");
                    }
                }
            }
        }
    }

    /// <summary>Every place a type mentions a System.Net type: base types, fields, signatures, locals and IL operands.</summary>
    private static IEnumerable<string> NetworkingUses(Type type)
    {
        var declared = new List<(string Where, Type? Type)> { ("base type", type.BaseType) };
        declared.AddRange(type.GetInterfaces().Select(i => ("interface", (Type?)i)));
        declared.AddRange(type.GetFields(Declared).Select(f => ($"field {f.Name}", (Type?)f.FieldType)));
        foreach (var (where, used) in declared)
        {
            if (IsNetworking(used))
            {
                yield return $"{where} is {used}";
            }
        }

        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            foreach (var used in SignatureTypes(method).Where(IsNetworking))
            {
                yield return $"{method.Name} signature uses {used}";
            }

            var body = method.GetMethodBody();
            if (body is null)
            {
                continue;
            }

            foreach (var local in body.LocalVariables.Where(l => IsNetworking(l.LocalType)))
            {
                yield return $"{method.Name} has a local {local.LocalType}";
            }

            foreach (var member in ReferencedMembers(method, body.GetILAsByteArray() ?? []))
            {
                if (Mentions(member))
                {
                    yield return $"{method.Name} references {member.DeclaringType}.{member}";
                }
            }
        }
    }

    private static IEnumerable<Type> SignatureTypes(MethodBase method)
    {
        if (method is MethodInfo info)
        {
            yield return info.ReturnType;
        }

        foreach (var parameter in method.GetParameters())
        {
            yield return parameter.ParameterType;
        }

        if (method.IsGenericMethod)
        {
            foreach (var argument in method.GetGenericArguments())
            {
                yield return argument;
            }
        }
    }

    private static bool Mentions(MemberInfo member) => member switch
    {
        Type type => IsNetworking(type),
        FieldInfo field => IsNetworking(field.DeclaringType) || IsNetworking(field.FieldType),
        MethodBase method => IsNetworking(method.DeclaringType) || SignatureTypes(method).Any(IsNetworking),
        _ => IsNetworking(member.DeclaringType),
    };

    private static bool IsNetworking(Type? type)
    {
        if (type is null || type.IsGenericParameter)
        {
            return false;
        }

        if (type.HasElementType)
        {
            return IsNetworking(type.GetElementType());
        }

        if (type.IsConstructedGenericType && type.GetGenericArguments().Any(IsNetworking))
        {
            return true;
        }

        var ns = type.Namespace ?? string.Empty;
        return ns == "System.Net" || ns.StartsWith("System.Net.", StringComparison.Ordinal);
    }

    /// <summary>Fields, methods and types an IL body refers to (call, newobj, ldfld, ldtoken, castclass, …).</summary>
    private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method, byte[] il)
    {
        var typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            var value = (short)il[i++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[i++]);
            }

            var opcode = OpCodesByValue[value];
            switch (opcode.OperandType)
            {
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                    var member = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArguments, methodArguments);
                    i += 4;
                    if (member is not null)
                    {
                        yield return member;
                    }

                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + (4 * BitConverter.ToInt32(il, i));
                    break;
                default:
                    // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR
                    i += 4;
                    break;
            }
        }
    }
}

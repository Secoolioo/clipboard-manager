using System.Reflection;
using System.Runtime.InteropServices;

namespace ClipboardManager.Tests.Privacy;

/// <summary>
/// "No network code" as a checked property: our assemblies reference no networking assembly and
/// call native code only in an allow-list of local Windows libraries.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class NoNetworkTests
{
    private static readonly Assembly[] Ours =
    [
        typeof(Core.AppPaths).Assembly,
        typeof(ClipboardManager.Shell.HostWindow).Assembly,
    ];

    private static readonly HashSet<string> AllowedNativeLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "user32.dll", "kernel32.dll", "shell32.dll", "shcore.dll", "dwmapi.dll", "wtsapi32.dll", "psapi.dll",
    };

    [Fact]
    public void No_networking_assemblies_are_referenced()
    {
        foreach (var assembly in Ours)
        {
            var networking = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => n.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase) && n is not "System.Net.Primitives")
                .ToList();
            Assert.True(networking.Count == 0, $"{assembly.GetName().Name} references {string.Join(", ", networking)}");
        }
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
}

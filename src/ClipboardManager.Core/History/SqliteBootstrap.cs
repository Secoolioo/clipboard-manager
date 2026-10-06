using System.Reflection;
using System.Runtime.InteropServices;

namespace ClipboardManager.Core.History;

/// <summary>
/// Uses the SQLite that ships with Windows (System32\winsqlite3.dll, signed by Microsoft) so the
/// single-file EXE never extracts an unsigned third-party native library. The DLL is always loaded
/// from System32, never from the application directory (prevents DLL planting next to the EXE).
/// </summary>
public static class SqliteBootstrap
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            var providerAssembly = typeof(SQLitePCL.SQLite3Provider_winsqlite3).Assembly;
            NativeLibrary.SetDllImportResolver(providerAssembly, ResolveFromSystem32);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            _initialized = true;
        }
    }

    internal static string SystemLibraryPath => Path.Combine(Environment.SystemDirectory, "winsqlite3.dll");

    private static IntPtr ResolveFromSystem32(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName.StartsWith("winsqlite3", StringComparison.OrdinalIgnoreCase))
        {
            return NativeLibrary.Load(SystemLibraryPath);
        }

        return IntPtr.Zero;
    }
}

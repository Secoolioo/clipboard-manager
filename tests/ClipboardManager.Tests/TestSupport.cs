using ClipboardManager.Core;

namespace ClipboardManager.Tests;

/// <summary>A throw-away data directory under %TEMP%, deleted after the test.</summary>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Paths = new AppPaths(Path);
    }

    public string Path { get; }

    public AppPaths Paths { get; }

    /// <summary>True if any file in the data directory contains <paramref name="needle"/> as UTF-8 or UTF-16.</summary>
    public bool AnyFileContains(string needle)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(needle);
        var utf16 = System.Text.Encoding.Unicode.GetBytes(needle);
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            if (bytes.AsSpan().IndexOf(utf8) >= 0 || bytes.AsSpan().IndexOf(utf16) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Canary
{
    /// <summary>A unique marker that must never show up where it should not.</summary>
    public static string Create() => "canary-" + Guid.NewGuid().ToString("N");
}

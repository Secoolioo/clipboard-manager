using System.Runtime.InteropServices;

namespace ClipboardManager.Interop;

internal static partial class Kernel32
{
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint WER_FAULT_REPORTING_FLAG_NOHEAP = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    /// <summary>Crash reports must not contain the heap (it holds clipboard history).</summary>
    [LibraryImport("kernel32.dll")]
    public static partial int WerSetFlags(uint dwFlags);

    /// <summary>File name of the process owning <paramref name="hwnd"/>, or null.</summary>
    public static string? ProcessExeOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        _ = User32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : ProcessExe(pid);
    }

    public static string? ProcessExe(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            unsafe
            {
                var buffer = stackalloc char[1024];
                uint size = 1024;
                return QueryFullProcessImageName(process, 0, buffer, ref size)
                    ? Path.GetFileName(new string(buffer, 0, (int)size))
                    : null;
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}

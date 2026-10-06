using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Shell;

/// <summary>Per-user Start menu entry, so a portable EXE can be found again after "Exit". No admin rights.</summary>
internal sealed class StartMenuShortcut
{
    private const string Category = "StartMenu";
    private readonly string _exePath;
    private readonly FileLog _log;

    public StartMenuShortcut(string exePath, FileLog log)
    {
        _exePath = exePath;
        _log = log;
    }

    public static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "Clipboard Manager.lnk");

    public static bool Exists => File.Exists(LinkPath);

    public bool Create()
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(_exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(_exePath)!);
            link.SetDescription("Clipboard history for Windows");
            link.SetIconLocation(_exePath, 0);
            ((IPersistFile)link).Save(LinkPath, true);
            Marshal.FinalReleaseComObject(link);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            _log.Warning(Category, "Could not create the Start menu shortcut", ex);
            return false;
        }
    }

    public void Remove()
    {
        try
        {
            File.Delete(LinkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(Category, "Could not remove the Start menu shortcut", ex);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

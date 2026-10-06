using ClipboardManager.Interop;

namespace ClipboardManager.Clipboard;

/// <summary>Registered clipboard formats that tell clipboard monitors to stay away.</summary>
internal static class ClipboardFormats
{
    /// <summary>Microsoft: data in this format excludes everything from history and cloud sync (presence counts).</summary>
    public static readonly uint ExcludeFromMonitoring = User32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    /// <summary>Microsoft: DWORD 0 means "do not put into clipboard history".</summary>
    public static readonly uint CanIncludeInHistory = User32.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    /// <summary>De-facto convention (KeePass, Ditto, CopyQ): presence means "ignore".</summary>
    public static readonly uint ViewerIgnore = User32.RegisterClipboardFormat("Clipboard Viewer Ignore");
}

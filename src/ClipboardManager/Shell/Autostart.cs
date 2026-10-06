using ClipboardManager.Core.Diagnostics;
using Microsoft.Win32;

namespace ClipboardManager.Shell;

internal enum AutostartState
{
    Off,
    On,

    /// <summary>The Run entry exists but the user disabled it in Task Manager / Settings.</summary>
    DisabledInWindows,

    /// <summary>The Run entry starts another copy of the EXE.</summary>
    OtherLocation,
}

/// <summary>
/// Per-user autostart through HKCU\...\Run (visible and switchable in Task Manager and Settings).
/// The registry is the only source of truth. The app writes automatically only on first run and to
/// repair a path after a manual start; everything else happens on an explicit click.
/// </summary>
internal sealed class Autostart
{
    public const string ValueName = "Secoolioo.ClipboardManager";
    public const string AutostartArgument = "--autostart";

    private const string Category = "Autostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string DecisionKey = @"Software\Secoolioo\ClipboardManager";

    private readonly RegistryKey _root;
    private readonly string _exePath;
    private readonly FileLog _log;

    public Autostart(string exePath, FileLog log, RegistryKey? root = null)
    {
        _exePath = Path.GetFullPath(exePath);
        _log = log;
        _root = root ?? Registry.CurrentUser;
    }

    public string ExePath => _exePath;

    public bool FirstRunDone
    {
        get => ReadDecision("FirstRunDone") == 1;
        private set => WriteDecision("FirstRunDone", value ? 1 : 0);
    }

    public AutostartState State
    {
        get
        {
            var command = ReadRunCommand();
            if (command is null)
            {
                return AutostartState.Off;
            }

            if (!string.Equals(ExtractPath(command), _exePath, StringComparison.OrdinalIgnoreCase))
            {
                return AutostartState.OtherLocation;
            }

            return IsApproved() ? AutostartState.On : AutostartState.DisabledInWindows;
        }
    }

    /// <summary>Paths from which an autostart entry would soon point to nothing.</summary>
    public bool IsVolatileLocation => IsVolatile(_exePath);

    public bool IsInDownloads =>
        _exePath.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// First start: register unless the user ever decided or Windows already has an entry. A start
    /// from a temporary location (ZIP, %TEMP%, USB, network) does not count as the first run.
    /// </summary>
    public bool RegisterOnFirstRun()
    {
        if (FirstRunDone || IsVolatileLocation)
        {
            return false;
        }

        FirstRunDone = true;
        if (ReadDecision("AutostartChoice") is not null || ReadRunCommand() is not null || HasApprovalValue())
        {
            return false;
        }

        WriteRun();
        _log.Info(Category, "Registered for autostart on first run");
        return true;
    }

    /// <summary>After a manual start from a new location: fix an entry that points to a deleted EXE.</summary>
    public bool RepairIfBroken()
    {
        var command = ReadRunCommand();
        if (command is null || IsVolatileLocation)
        {
            return false;
        }

        var path = ExtractPath(command);
        if (string.Equals(path, _exePath, StringComparison.OrdinalIgnoreCase) || File.Exists(path))
        {
            return false;
        }

        WriteRun();
        _log.Info(Category, "Autostart entry pointed to a missing file and was repaired");
        return true;
    }

    /// <summary>Explicit user action (settings, welcome window). Re-enabling also clears a Task Manager "disabled" mark.</summary>
    public void SetEnabled(bool enabled)
    {
        WriteDecision("AutostartChoice", enabled ? 1 : 0);
        if (enabled)
        {
            WriteRun();
            DeleteApprovalValue();
        }
        else
        {
            using (var run = _root.OpenSubKey(RunKey, writable: true))
            {
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            DeleteApprovalValue();
        }
    }

    /// <summary>"Remove everything": autostart entries and our decision key.</summary>
    public void RemoveAll()
    {
        using (var run = _root.OpenSubKey(RunKey, writable: true))
        {
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        DeleteApprovalValue();
        _root.DeleteSubKeyTree(DecisionKey, throwOnMissingSubKey: false);
    }

    internal static string ExtractPath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }

        var exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? trimmed[..(exe + 4)] : trimmed;
    }

    internal static bool IsVolatile(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        var temp = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            return drive.DriveType is DriveType.Removable or DriveType.Network or DriveType.CDRom;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private string Command => $"\"{_exePath}\" {AutostartArgument}";

    private void WriteRun()
    {
        using var run = _root.CreateSubKey(RunKey, writable: true);
        run.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    private string? ReadRunCommand()
    {
        using var run = _root.OpenSubKey(RunKey);
        return run?.GetValue(ValueName) as string;
    }

    private bool HasApprovalValue()
    {
        using var approved = _root.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is not null;
    }

    /// <summary>Undocumented but stable: missing value or an even first byte means enabled.</summary>
    private bool IsApproved()
    {
        using var approved = _root.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    private void DeleteApprovalValue()
    {
        using var approved = _root.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private int? ReadDecision(string name)
    {
        using var key = _root.OpenSubKey(DecisionKey);
        return key?.GetValue(name) as int?;
    }

    private void WriteDecision(string name, int value)
    {
        using var key = _root.CreateSubKey(DecisionKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}

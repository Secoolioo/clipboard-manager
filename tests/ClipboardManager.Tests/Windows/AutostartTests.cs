using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Shell;
using Microsoft.Win32;

namespace ClipboardManager.Tests.Windows;

/// <summary>Runs against an isolated HKCU\Software\Secoolioo\ClipboardManager.Tests\{guid} subtree, never the real Run key.</summary>
[Trait("Category", "Windows")]
public sealed class AutostartTests : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _rootPath = @"Software\Secoolioo\ClipboardManager.Tests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly string _exe;

    public AutostartTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _exe = Path.Combine(local, "cm-tests", "Program Files", "ClipboardManager.exe");
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
    }

    private Autostart Create(string? exe = null) => new(exe ?? _exe, FileLog.Null, _root);

    private string? RunValue()
    {
        using var run = _root.OpenSubKey(RunKey);
        return run?.GetValue(Autostart.ValueName) as string;
    }

    private void DisableInTaskManager()
    {
        using var approved = _root.CreateSubKey(ApprovedKey, writable: true);
        approved.SetValue(Autostart.ValueName, new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, RegistryValueKind.Binary);
    }

    [Fact]
    public void First_run_registers_a_quoted_command_with_the_autostart_flag()
    {
        var autostart = Create();

        Assert.True(autostart.RegisterOnFirstRun());

        Assert.Equal($"\"{_exe}\" --autostart", RunValue());
        Assert.Equal(AutostartState.On, autostart.State);
        Assert.True(autostart.FirstRunDone);
    }

    [Fact]
    public void Second_start_never_registers_again()
    {
        var autostart = Create();
        autostart.RegisterOnFirstRun();
        autostart.SetEnabled(false);

        Assert.False(Create().RegisterOnFirstRun());
        Assert.Null(RunValue());
    }

    [Fact]
    public void A_start_from_a_temporary_location_does_not_count_as_first_run()
    {
        var temp = Create(Path.Combine(Path.GetTempPath(), "Temp1_ClipboardManager.zip", "ClipboardManager.exe"));

        Assert.False(temp.RegisterOnFirstRun());
        Assert.False(temp.FirstRunDone);
        Assert.Null(RunValue());

        Assert.True(Create().RegisterOnFirstRun());
    }

    [Fact]
    public void Disabling_in_task_manager_is_reported_and_never_overridden_automatically()
    {
        var autostart = Create();
        autostart.RegisterOnFirstRun();
        DisableInTaskManager();

        Assert.Equal(AutostartState.DisabledInWindows, autostart.State);
        Assert.False(Create().RegisterOnFirstRun());
        Assert.False(Create().RepairIfBroken());
        Assert.Equal(AutostartState.DisabledInWindows, Create().State);
    }

    [Fact]
    public void Enabling_in_the_app_clears_the_task_manager_mark()
    {
        var autostart = Create();
        autostart.RegisterOnFirstRun();
        DisableInTaskManager();

        autostart.SetEnabled(true);

        Assert.Equal(AutostartState.On, autostart.State);
    }

    [Fact]
    public void User_decision_survives_a_deleted_data_folder()
    {
        var autostart = Create();
        autostart.RegisterOnFirstRun();
        autostart.SetEnabled(false);

        // Simulate a fresh install state except for the decision marker.
        using (var decision = _root.OpenSubKey(@"Software\Secoolioo\ClipboardManager", writable: true))
        {
            decision!.DeleteValue("FirstRunDone");
        }

        Assert.False(Create().RegisterOnFirstRun());
        Assert.Null(RunValue());
    }

    [Fact]
    public void Moved_exe_repairs_an_entry_that_points_to_a_missing_file()
    {
        Create(Path.Combine(Path.GetDirectoryName(_exe)!, "old", "ClipboardManager.exe")).RegisterOnFirstRun();

        var moved = Create();
        Assert.Equal(AutostartState.OtherLocation, moved.State);
        Assert.True(moved.RepairIfBroken());
        Assert.Equal(AutostartState.On, moved.State);
    }

    [Fact]
    public void Entry_pointing_to_another_existing_copy_is_left_alone()
    {
        var otherDirectory = Path.Combine(Path.GetTempPath(), "cm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDirectory);
        var other = Path.Combine(otherDirectory, "ClipboardManager.exe");
        File.WriteAllText(other, string.Empty);
        try
        {
            using (var run = _root.CreateSubKey(RunKey, writable: true))
            {
                run.SetValue(Autostart.ValueName, $"\"{other}\" --autostart");
            }

            var autostart = Create();
            Assert.Equal(AutostartState.OtherLocation, autostart.State);
            Assert.False(autostart.RepairIfBroken());
        }
        finally
        {
            Directory.Delete(otherDirectory, recursive: true);
        }
    }

    [Fact]
    public void An_updated_exe_at_the_same_path_keeps_the_entry_and_the_windows_decision()
    {
        Create().RegisterOnFirstRun();
        var command = RunValue();

        // The updater replaces the file in place: a new process with the same path starts.
        var updated = Create();
        Assert.Equal(AutostartState.On, updated.State);
        Assert.False(updated.RegisterOnFirstRun());
        Assert.False(updated.RepairIfBroken());
        Assert.Equal(command, RunValue());

        DisableInTaskManager();
        var updatedAgain = Create();
        Assert.False(updatedAgain.RepairIfBroken());
        Assert.Equal(AutostartState.DisabledInWindows, updatedAgain.State);
        Assert.Equal(command, RunValue());
    }

    /// <summary>
    /// The first-run rule: permanent folders register (Downloads only gets a tip), places that are
    /// gone soon do not – ZIP and installer extraction under %TEMP%, network shares. Removable and
    /// network drives are skipped too because they may not be there at sign-in.
    /// </summary>
    [Theory]
    [InlineData("Downloads", false)]
    [InlineData("Desktop", false)]
    [InlineData(@"AppData\Local\Programs\ClipboardManager", false)]
    [InlineData(@"OneDrive\Desktop", false)]
    [InlineData("%TEMP%\\Temp1_ClipboardManager-x64.zip", true)]
    [InlineData("%TEMP%\\7zO8A3F2C1D", true)]
    [InlineData(@"\\server\share\tools", true)]
    public void Only_temporary_locations_are_volatile(string folder, bool volatileLocation)
    {
        var directory = folder.StartsWith('%')
            ? Path.Combine(Path.GetTempPath(), folder["%TEMP%\\".Length..])
            : folder.StartsWith(@"\\", StringComparison.Ordinal)
                ? folder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder);

        Assert.Equal(volatileLocation, Autostart.IsVolatile(Path.Combine(directory, "ClipboardManager.exe")));
    }

    [Fact]
    public void Remove_all_cleans_every_value()
    {
        var autostart = Create();
        autostart.RegisterOnFirstRun();
        DisableInTaskManager();

        autostart.RemoveAll();

        Assert.Null(RunValue());
        Assert.Equal(AutostartState.Off, autostart.State);
        Assert.Null(_root.OpenSubKey(@"Software\Secoolioo\ClipboardManager"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\ClipboardManager.exe\" --autostart", "C:\\Program Files\\App\\ClipboardManager.exe")]
    [InlineData("C:\\Tools\\ClipboardManager.exe --autostart", "C:\\Tools\\ClipboardManager.exe")]
    public void Command_path_extraction(string command, string expected) => Assert.Equal(expected, Autostart.ExtractPath(command));
}

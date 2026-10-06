using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Tests.Privacy;

[Trait("Category", "Privacy")]
public sealed class LogPrivacyTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Exception_messages_are_never_written()
    {
        var canary = Canary.Create();
        var log = new FileLog(_dir.Paths.LogDirectory, LogLevel.Debug);

        try
        {
            throw new InvalidOperationException("input was " + canary, new FormatException(canary));
        }
        catch (InvalidOperationException ex)
        {
            log.Error("Test", "Operation failed", ex);
        }

        var text = File.ReadAllText(Path.Combine(_dir.Paths.LogDirectory, "app.log"));
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("System.FormatException", text, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_rotates_and_stays_bounded()
    {
        var log = new FileLog(_dir.Paths.LogDirectory, LogLevel.Debug);
        var line = new string('x', 1000);
        for (var i = 0; i < 2500; i++)
        {
            log.Info("Test", line);
        }

        var files = Directory.GetFiles(_dir.Paths.LogDirectory);
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= FileLog.MaxFileBytes + 2048));
    }

    [Fact]
    public void Default_level_drops_info_and_debug()
    {
        var log = new FileLog(_dir.Paths.LogDirectory, LogLevel.Warning);
        log.Info("Test", "info");
        log.Debug("Test", "debug");

        Assert.False(File.Exists(Path.Combine(_dir.Paths.LogDirectory, "app.log")));
    }
}

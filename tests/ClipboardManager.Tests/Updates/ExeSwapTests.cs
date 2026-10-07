using ClipboardManager.Core.Updates;

namespace ClipboardManager.Tests.Updates;

/// <summary>The in-place EXE swap against temp files. Nothing is started.</summary>
[Trait("Category", "Update")]
public sealed class ExeSwapTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly ExeSwap _swap;

    public ExeSwapTests()
    {
        _swap = new ExeSwap(Path.Combine(_dir.Path, "ClipboardManager.exe"), attempts: 3, TimeSpan.FromMilliseconds(10));
        File.WriteAllText(_swap.ExePath, "old version");
        File.WriteAllText(_swap.NewPath, "new version");
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>Opens the file like the loader opens a running image: readable, renamable, not writable.</summary>
    private FileStream HoldLikeARunningImage() => new(_swap.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    [Fact]
    public void The_running_exe_moves_aside_and_the_download_takes_its_name()
    {
        using (HoldLikeARunningImage())
        {
            _swap.Apply();
        }

        Assert.Equal("new version", File.ReadAllText(_swap.ExePath));
        Assert.Equal("old version", File.ReadAllText(_swap.OldPath));
        Assert.False(File.Exists(_swap.NewPath));
    }

    [Fact]
    public void A_leftover_old_exe_from_an_earlier_update_is_replaced()
    {
        File.WriteAllText(_swap.OldPath, "older version");

        _swap.Apply();

        Assert.Equal("new version", File.ReadAllText(_swap.ExePath));
        Assert.Equal("old version", File.ReadAllText(_swap.OldPath));
    }

    [Fact]
    public void A_locked_download_rolls_the_swap_back()
    {
        using (new FileStream(_swap.NewPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (HoldLikeARunningImage())
        {
            Assert.ThrowsAny<IOException>(_swap.Apply);
        }

        Assert.Equal("old version", File.ReadAllText(_swap.ExePath));
        Assert.False(File.Exists(_swap.OldPath));
        Assert.Equal("new version", File.ReadAllText(_swap.NewPath));
    }

    [Fact]
    public void Nothing_changes_without_a_download()
    {
        File.Delete(_swap.NewPath);

        Assert.Throws<FileNotFoundException>(_swap.Apply);

        Assert.Equal("old version", File.ReadAllText(_swap.ExePath));
        Assert.False(File.Exists(_swap.OldPath));
    }

    [Fact]
    public void Revert_restores_the_previous_exe_when_the_new_one_cannot_start()
    {
        _swap.Apply();

        _swap.Revert();

        Assert.Equal("old version", File.ReadAllText(_swap.ExePath));
        Assert.False(File.Exists(_swap.OldPath));
    }

    [Fact]
    public void The_old_exe_is_deleted_best_effort()
    {
        _swap.Apply();

        using (new FileStream(_swap.OldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(_swap.TryDeleteOld());
        }

        Assert.True(_swap.TryDeleteOld());
        Assert.False(File.Exists(_swap.OldPath));
        Assert.True(_swap.TryDeleteOld());
        Assert.Equal("new version", File.ReadAllText(_swap.ExePath));
    }

    [Fact]
    public void A_rejected_download_is_discarded_without_touching_the_exe()
    {
        Assert.True(_swap.TryDiscardDownload());

        Assert.False(File.Exists(_swap.NewPath));
        Assert.Equal("old version", File.ReadAllText(_swap.ExePath));
        Assert.True(_swap.TryDiscardDownload());
    }
}

namespace ClipboardManager.Core.Updates;

/// <summary>
/// Puts a verified download ("*.new") in place of the EXE. No running image is ever renamed or
/// replaced: a single-file app keeps reading its assemblies from its own path while it runs. So the
/// download is started as it is, waits until the previous version has exited, moves the previous
/// EXE aside to "*.old" and copies itself to the EXE path. The path stays the same, so the autostart
/// entry and shortcuts keep working. Virus scanners briefly lock fresh files, so every step retries.
/// </summary>
public sealed class ExeSwap
{
    /// <summary>Suffix of the verified download next to the EXE.</summary>
    public const string DownloadSuffix = ".new";

    private readonly int _attempts;
    private readonly TimeSpan _retryDelay;

    public ExeSwap(string exePath, int attempts = 10, TimeSpan? retryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ExePath = Path.GetFullPath(exePath);
        _attempts = attempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public string ExePath { get; }

    /// <summary>Where the verified download is written; it is also what runs to install itself.</summary>
    public string NewPath => ExePath + DownloadSuffix;

    /// <summary>The previous EXE after the swap, deleted by the next start.</summary>
    public string OldPath => ExePath + ".old";

    /// <summary>
    /// EXE → *.old, copy of *.new → EXE. Runs in the downloaded EXE after the previous version
    /// exited; the download is copied, not moved, because it is the running image. On failure the
    /// previous EXE is put back and the exception rethrown.
    /// </summary>
    public void Install()
    {
        if (!File.Exists(NewPath))
        {
            throw new FileNotFoundException("No downloaded update to install");
        }

        var hadExe = File.Exists(ExePath);
        if (hadExe)
        {
            Retry(() => File.Delete(OldPath));
            Retry(() => File.Move(ExePath, OldPath));
        }

        try
        {
            Retry(() => File.Copy(NewPath, ExePath, overwrite: true));
        }
        catch (Exception ex) when (IsFileError(ex) && hadExe)
        {
            // Replaces a partial copy, too: some working EXE must stay at the path.
            Retry(() => File.Move(OldPath, ExePath, overwrite: true));
            throw;
        }
    }

    /// <summary>Best effort, never throws: the previous EXE after an update (it may still be closing).</summary>
    public bool TryDeleteOld()
    {
        try
        {
            Retry(() => File.Delete(OldPath));
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return false;
        }
    }

    /// <summary>Best effort, never throws: a rejected download, an installed one or one a crash left behind.</summary>
    public bool TryDiscardDownload()
    {
        try
        {
            Retry(() => File.Delete(NewPath));
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return false;
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < _attempts && IsFileError(ex) && ex is not FileNotFoundException)
            {
                Thread.Sleep(_retryDelay);
            }
        }
    }
}

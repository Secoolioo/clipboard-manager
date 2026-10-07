namespace ClipboardManager.Core.Updates;

/// <summary>
/// Replaces the EXE of the running process in place. Windows refuses to overwrite or delete a
/// running image but allows renaming it, so the running file moves aside to "*.old" and the
/// verified download ("*.new") takes its name. The path stays the same, so the autostart entry and
/// shortcuts keep working. Virus scanners briefly lock fresh files, so every step retries.
/// </summary>
public sealed class ExeSwap
{
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

    /// <summary>Where the verified download is written before the swap.</summary>
    public string NewPath => ExePath + ".new";

    /// <summary>The previous EXE after the swap, deleted by the next start.</summary>
    public string OldPath => ExePath + ".old";

    /// <summary>EXE → *.old, *.new → EXE. On failure the EXE is put back and the exception rethrown.</summary>
    public void Apply()
    {
        if (!File.Exists(NewPath))
        {
            throw new FileNotFoundException("No downloaded update to install");
        }

        Retry(() => File.Delete(OldPath));
        Retry(() => File.Move(ExePath, OldPath));
        try
        {
            Retry(() => File.Move(NewPath, ExePath));
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            try
            {
                Retry(() => File.Move(OldPath, ExePath));
            }
            catch (Exception rollback) when (IsFileError(rollback))
            {
                // Some EXE must stay at the path (autostart, shortcuts): the verified new one will do.
                Retry(() => File.Move(NewPath, ExePath));
            }

            throw;
        }
    }

    /// <summary>Undoes <see cref="Apply"/> when the new EXE could not be started: the previous EXE gets its name back.</summary>
    public void Revert() => Retry(() => File.Move(OldPath, ExePath, overwrite: true));

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

    /// <summary>Best effort, never throws, no retries: a rejected download or one a crash left behind.</summary>
    public bool TryDiscardDownload()
    {
        try
        {
            File.Delete(NewPath);
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

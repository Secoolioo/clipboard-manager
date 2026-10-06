namespace ClipboardManager.Core.Monitoring;

public enum MonitoringMode
{
    Active,
    PausedUntil,
    PausedIndefinitely,
}

/// <summary>
/// Single source of truth for "is the clipboard being recorded". Timed pauses compare against the
/// wall clock, so they also end correctly after sleep/hibernate.
/// </summary>
public sealed class MonitoringState
{
    private readonly TimeProvider _time;
    private DateTimeOffset _pausedUntil;
    private bool _indefinite;

    public MonitoringState(TimeProvider time, bool pausedIndefinitely = false)
    {
        _time = time;
        _indefinite = pausedIndefinitely;
    }

    public event EventHandler? Changed;

    public bool IgnoreNext { get; private set; }

    public MonitoringMode Mode
    {
        get
        {
            if (_indefinite)
            {
                return MonitoringMode.PausedIndefinitely;
            }

            return _time.GetUtcNow() < _pausedUntil ? MonitoringMode.PausedUntil : MonitoringMode.Active;
        }
    }

    public bool IsRecording => Mode == MonitoringMode.Active;

    public DateTimeOffset? PausedUntil => Mode == MonitoringMode.PausedUntil ? _pausedUntil : null;

    public bool IsPausedIndefinitely => _indefinite;

    public void PauseFor(TimeSpan duration)
    {
        _indefinite = false;
        _pausedUntil = _time.GetUtcNow() + duration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PauseIndefinitely()
    {
        _indefinite = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        _indefinite = false;
        _pausedUntil = DateTimeOffset.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void IgnoreNextCopy()
    {
        IgnoreNext = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called for every clipboard change; consumes a pending "ignore next copy".</summary>
    public Capture.SkipReason OnClipboardChanged()
    {
        if (!IsRecording)
        {
            return Capture.SkipReason.Paused;
        }

        if (IgnoreNext)
        {
            IgnoreNext = false;
            Changed?.Invoke(this, EventArgs.Empty);
            return Capture.SkipReason.IgnoredOnce;
        }

        return Capture.SkipReason.None;
    }

    /// <summary>Raises <see cref="Changed"/> if a timed pause has run out (call from a one-shot timer or after resume).</summary>
    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);
}

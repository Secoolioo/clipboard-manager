using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Hosting;

/// <summary>A notification-area message (rendered as a toast). Never carries clipboard content.</summary>
internal readonly record struct TrayNotice(string Title, string Text, bool Warning);

/// <summary>
/// Quiet start after sign-in ("--autostart"). Capture, hotkey and tray icon start right away;
/// work that only makes the app nicer (popup pre-warming, cleanup) waits for one one-shot delay or
/// until the user first opens the app, whichever comes first. Until then the process runs below
/// normal priority, and notices raised meanwhile wait until the user first opens the popup or the
/// tray menu (at the latest when the delay ends) instead of popping up at sign-in. A manual start
/// runs everything immediately.
/// </summary>
internal sealed class StartupWarmup : IDisposable
{
    /// <summary>Long enough for sign-in, Explorer and the other startup apps to settle.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(45);

    private const string Category = "Startup";

    private readonly Action<TrayNotice> _notify;
    private readonly FileLog _log;
    private readonly Platform _platform;
    private readonly List<Action> _deferred = [];
    private readonly List<TrayNotice> _held = [];
    private IDisposable? _timer;
    private bool _quiet;
    private bool _lowered;

    public StartupWarmup(bool quiet, Action<TrayNotice> notify, FileLog log, Platform? platform = null)
    {
        _quiet = quiet;
        _notify = notify;
        _log = log;
        _platform = platform ?? Platform.Default;
    }

    /// <summary>True until the delay ended or the user first opened the app.</summary>
    public bool IsQuiet => _quiet;

    /// <summary>
    /// Starts the delay and lowers the priority. Called once capture, hotkey and tray icon are up,
    /// so the parts that make the app reachable do not start starved.
    /// </summary>
    public void Begin()
    {
        if (!_quiet || _timer is not null)
        {
            return;
        }

        LowerPriority();
        _timer = _platform.StartTimer(QuietPeriod, () => Finish("delay elapsed"));
        _log.Info(Category, $"Quiet start: warm-up deferred for {QuietPeriod.TotalSeconds:F0} s");
    }

    /// <summary>Runs <paramref name="work"/> now, or once at the end of the quiet phase (queued twice, it still runs once).</summary>
    public void Defer(Action work)
    {
        if (!_quiet)
        {
            work();
        }
        else if (!_deferred.Contains(work))
        {
            _deferred.Add(work);
        }
    }

    /// <summary>Shows a notice now; one raised during the quiet phase waits until the user first opens the app.</summary>
    public void Notify(TrayNotice notice)
    {
        if (_quiet)
        {
            _held.Add(notice);
        }
        else
        {
            _notify(notice);
        }
    }

    /// <summary>The user opened the popup or the tray menu: finish the warm-up now and show held notices.</summary>
    public void UserEngaged() => Finish("opened by the user");

    public void Dispose()
    {
        _quiet = false;
        _timer?.Dispose();
        _timer = null;
        _deferred.Clear();
        _held.Clear();
    }

    private void Finish(string reason)
    {
        if (!_quiet)
        {
            return;
        }

        _quiet = false;
        _timer?.Dispose();
        _timer = null;
        RestorePriority();
        _log.Info(Category, $"Quiet phase ended ({reason})");

        var work = _deferred.ToArray();
        _deferred.Clear();
        foreach (var item in work)
        {
            item();
        }

        // Held notices (e.g. "shortcut taken") must not wait forever: the user may rely on exactly
        // the shortcut that did not work. 45 s after sign-in is no longer "at login".
        var held = _held.ToArray();
        _held.Clear();
        foreach (var notice in held)
        {
            _notify(notice);
        }
    }

    private void LowerPriority()
    {
        try
        {
            // Only from the default class: a priority someone chose on purpose stays as it is.
            if (_platform.GetPriority() == ProcessPriorityClass.Normal)
            {
                _platform.SetPriority(ProcessPriorityClass.BelowNormal);
                _lowered = true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _log.Debug(Category, "Could not lower the process priority");
        }
    }

    private void RestorePriority()
    {
        if (!_lowered)
        {
            return;
        }

        _lowered = false;
        try
        {
            // Changed meanwhile (for example in Task Manager): that choice wins.
            if (_platform.GetPriority() == ProcessPriorityClass.BelowNormal)
            {
                _platform.SetPriority(ProcessPriorityClass.Normal);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _log.Debug(Category, "Could not restore the process priority");
        }
    }

    /// <summary>The operating-system side: a one-shot timer and the process priority. Replaced in tests.</summary>
    internal sealed record Platform(
        Func<TimeSpan, Action, IDisposable> StartTimer,
        Func<ProcessPriorityClass> GetPriority,
        Action<ProcessPriorityClass> SetPriority)
    {
        public static Platform Default { get; } = new(StartOneShot, GetCurrentPriority, SetCurrentPriority);

        private static TimerStopper StartOneShot(TimeSpan delay, Action elapsed)
        {
            // Ticks once and stops: no periodic timer is left behind.
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                elapsed();
            };
            timer.Start();
            return new TimerStopper(timer);
        }

        private static ProcessPriorityClass GetCurrentPriority()
        {
            using var process = Process.GetCurrentProcess();
            return process.PriorityClass;
        }

        private static void SetCurrentPriority(ProcessPriorityClass priority)
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = priority;
        }
    }

    private sealed class TimerStopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

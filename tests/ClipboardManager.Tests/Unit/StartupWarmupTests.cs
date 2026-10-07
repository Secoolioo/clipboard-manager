using System.Diagnostics;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Hosting;

namespace ClipboardManager.Tests.Unit;

/// <summary>The quiet-start rules, against a fake timer and a fake process priority (nothing real is changed).</summary>
[Trait("Category", "Unit")]
public sealed class StartupWarmupTests
{
    private static readonly TrayNotice Notice = new("Clipboard Manager", "notice", Warning: true);

    private readonly FakePlatform _platform = new();
    private readonly List<TrayNotice> _shown = [];

    /// <summary>Like the app: created first, started once capture, hotkey and tray are up.</summary>
    private StartupWarmup Create(bool quiet)
    {
        var warmup = new StartupWarmup(quiet, _shown.Add, FileLog.Null, _platform.Create());
        warmup.Begin();
        return warmup;
    }

    [Fact]
    public void The_priority_is_lowered_only_when_the_quiet_phase_begins()
    {
        using var warmup = new StartupWarmup(quiet: true, _shown.Add, FileLog.Null, _platform.Create());
        var runs = 0;
        warmup.Defer(() => runs++);

        // Startup itself (settings, host window, hotkey, tray, capture) runs at normal priority.
        Assert.True(warmup.IsQuiet);
        Assert.Equal(0, runs);
        Assert.Equal(ProcessPriorityClass.Normal, _platform.Priority);
        Assert.Empty(_platform.Timers);

        warmup.Begin();
        warmup.Begin();

        Assert.Equal(ProcessPriorityClass.BelowNormal, _platform.Priority);
        Assert.Single(_platform.Timers);
    }

    [Fact]
    public void Manual_start_runs_work_and_shows_notices_immediately()
    {
        using var warmup = Create(quiet: false);
        var runs = 0;

        warmup.Defer(() => runs++);
        warmup.Notify(Notice);

        Assert.False(warmup.IsQuiet);
        Assert.Equal(1, runs);
        Assert.Equal([Notice], _shown);
        Assert.Empty(_platform.Timers);
        Assert.Equal(ProcessPriorityClass.Normal, _platform.Priority);
    }

    [Fact]
    public void Quiet_start_lowers_the_priority_and_holds_work_for_one_one_shot_delay()
    {
        using var warmup = Create(quiet: true);
        var order = new List<string>();

        warmup.Defer(() => order.Add("prewarm"));
        warmup.Defer(() => order.Add("cleanup"));

        Assert.True(warmup.IsQuiet);
        Assert.Empty(order);
        Assert.Equal(ProcessPriorityClass.BelowNormal, _platform.Priority);
        var timer = Assert.Single(_platform.Timers);
        Assert.Equal(StartupWarmup.QuietPeriod, timer.Delay);

        timer.Elapsed();

        Assert.False(warmup.IsQuiet);
        Assert.Equal(["prewarm", "cleanup"], order);
        Assert.Equal(ProcessPriorityClass.Normal, _platform.Priority);
        Assert.Single(_platform.Timers);

        warmup.Defer(() => order.Add("later"));
        Assert.Equal("later", order[^1]);
    }

    [Fact]
    public void Opening_the_app_early_finishes_the_warm_up_at_once_and_stops_the_timer()
    {
        using var warmup = Create(quiet: true);
        var runs = 0;
        warmup.Defer(() => runs++);

        warmup.UserEngaged();

        Assert.Equal(1, runs);
        Assert.Equal(ProcessPriorityClass.Normal, _platform.Priority);
        Assert.True(_platform.Timers[0].Stopped);

        // A tick that was already queued, or a second engagement, must not run anything again.
        _platform.Timers[0].Elapsed();
        warmup.UserEngaged();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Notices_raised_during_a_quiet_start_wait_until_the_user_opens_the_app()
    {
        using var warmup = Create(quiet: true);
        warmup.Notify(Notice);
        Assert.Empty(_shown);

        warmup.UserEngaged();
        Assert.Equal([Notice], _shown);

        warmup.UserEngaged();
        Assert.Single(_shown);
    }

    [Fact]
    public void Held_notices_are_shown_when_the_delay_ends_at_the_latest()
    {
        using var warmup = Create(quiet: true);
        warmup.Notify(Notice);

        _platform.Timers[0].Elapsed();
        Assert.Equal([Notice], _shown);

        // After the quiet phase a new notice is shown right away, and nothing is shown twice.
        var later = Notice with { Text = "later" };
        warmup.Notify(later);
        warmup.UserEngaged();
        Assert.Equal([Notice, later], _shown);
    }

    [Fact]
    public void The_same_work_queued_twice_runs_once()
    {
        using var warmup = Create(quiet: true);
        var runs = 0;
        void Prewarm() => runs++;

        warmup.Defer(Prewarm);
        warmup.Defer(Prewarm);
        warmup.UserEngaged();

        Assert.Equal(1, runs);
    }

    [Theory]
    [InlineData(ProcessPriorityClass.Idle)]
    [InlineData(ProcessPriorityClass.High)]
    public void A_priority_chosen_elsewhere_is_never_changed(ProcessPriorityClass chosen)
    {
        _platform.Priority = chosen;
        using var warmup = Create(quiet: true);

        Assert.Equal(chosen, _platform.Priority);
        warmup.UserEngaged();
        Assert.Equal(chosen, _platform.Priority);
    }

    [Fact]
    public void A_priority_changed_during_the_quiet_phase_is_kept()
    {
        using var warmup = Create(quiet: true);
        _platform.Priority = ProcessPriorityClass.AboveNormal;

        _platform.Timers[0].Elapsed();

        Assert.Equal(ProcessPriorityClass.AboveNormal, _platform.Priority);
    }

    [Fact]
    public void Failing_priority_calls_do_not_break_the_start()
    {
        _platform.Fail = true;
        using var warmup = Create(quiet: true);
        var runs = 0;
        warmup.Defer(() => runs++);

        _platform.Timers[0].Elapsed();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Shutdown_during_the_quiet_phase_drops_the_work_and_stops_the_timer()
    {
        var warmup = Create(quiet: true);
        var runs = 0;
        warmup.Defer(() => runs++);
        warmup.Notify(Notice);

        warmup.Dispose();
        _platform.Timers[0].Elapsed();
        warmup.UserEngaged();

        Assert.Equal(0, runs);
        Assert.Empty(_shown);
        Assert.True(_platform.Timers[0].Stopped);
    }

    private sealed class FakePlatform
    {
        public ProcessPriorityClass Priority { get; set; } = ProcessPriorityClass.Normal;

        public bool Fail { get; set; }

        public List<FakeTimer> Timers { get; } = [];

        public StartupWarmup.Platform Create() => new(
            (delay, elapsed) =>
            {
                var timer = new FakeTimer(delay, elapsed);
                Timers.Add(timer);
                return timer;
            },
            () => Fail ? throw new InvalidOperationException() : Priority,
            priority => Priority = Fail ? throw new System.ComponentModel.Win32Exception() : priority);
    }

    private sealed class FakeTimer(TimeSpan delay, Action elapsed) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;

        public Action Elapsed { get; } = elapsed;

        public bool Stopped { get; private set; }

        public void Dispose() => Stopped = true;
    }
}

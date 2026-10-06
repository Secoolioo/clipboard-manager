using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Hosting;

namespace ClipboardManager;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The controller is disposed in OnExit, which is the Application lifetime end.")]
internal sealed partial class App : Application
{
    private const string Category = "App";
    private const int MaxUnhandledPerMinute = 5;

    private readonly StartupOptions _options;
    private readonly Queue<long> _recentFailures = new();
    private AppController? _controller;

    public App(StartupOptions options)
    {
        _options = options;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _options.Log.Error(Category, "Unhandled exception (process terminating)", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _options.Log.Error(Category, "Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_options.SelfTest)
        {
            var exitCode = await SelfTest.RunAsync(this, _options).ConfigureAwait(true);
            Shutdown(exitCode);
            return;
        }

        _controller = new AppController(this, _options);
        _controller.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // WPF shuts the app down right after this; every capture is already committed,
        // so only a short, bounded close is needed.
        _controller?.ShutdownForSession();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Keep a background utility alive through isolated UI errors, but give up if errors repeat
    /// (a broken state should not loop forever).
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _options.Log.Error(Category, "Unhandled UI exception", e.Exception);
        var now = Environment.TickCount64;
        _recentFailures.Enqueue(now);
        while (_recentFailures.Count > 0 && now - _recentFailures.Peek() > 60_000)
        {
            _recentFailures.Dequeue();
        }

        if (_recentFailures.Count <= MaxUnhandledPerMinute)
        {
            e.Handled = true;
        }
    }
}

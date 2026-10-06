using ClipboardManager.Core.Settings;

namespace ClipboardManager.Hosting;

/// <summary>Current settings plus persistence; raises <see cref="Changed"/> with the previous value.</summary>
internal sealed class SettingsHolder
{
    private readonly SettingsStore _store;

    public SettingsHolder(SettingsStore store, AppSettings initial)
    {
        _store = store;
        Current = initial;
    }

    public event Action<AppSettings, AppSettings>? Changed;

    public AppSettings Current { get; private set; }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = Current;
        var next = change(previous).Normalize();
        if (next == previous)
        {
            return;
        }

        Current = next;
        _store.Save(next);
        Changed?.Invoke(previous, next);
    }
}

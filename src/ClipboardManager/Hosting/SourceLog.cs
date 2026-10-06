namespace ClipboardManager.Hosting;

/// <summary>
/// RAM-only list of recently seen source apps (names only, never content) and when an excluded
/// app was last ignored, so the settings page can show whether an exclusion rule ever fires.
/// </summary>
internal sealed class SourceLog
{
    private const int Capacity = 20;
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, DateTimeOffset> _lastIgnored = new(StringComparer.OrdinalIgnoreCase);

    public void Seen(string? exe)
    {
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        lock (_gate)
        {
            var existing = _recent.Find(exe);
            if (existing is not null)
            {
                _recent.Remove(existing);
            }

            _recent.AddFirst(exe);
            while (_recent.Count > Capacity)
            {
                _recent.RemoveLast();
            }
        }
    }

    public void Ignored(string? exe)
    {
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        lock (_gate)
        {
            _lastIgnored[exe] = DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent.Distinct(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    public DateTimeOffset? LastIgnored(string exe)
    {
        lock (_gate)
        {
            return _lastIgnored.TryGetValue(exe, out var when) ? when : null;
        }
    }
}

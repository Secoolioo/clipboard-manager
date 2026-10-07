using System.ComponentModel;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Text;
using ClipboardManager.Localization;

namespace ClipboardManager.Popup;

/// <summary>One line of the popup list. Only entries and the "more" toggle are selectable.</summary>
public abstract class PopupRow : INotifyPropertyChanged
{
    private int _position;
    private int _setSize;

    public event PropertyChangedEventHandler? PropertyChanged;

    public virtual bool IsSelectable => false;

    public virtual string AutomationName => string.Empty;

    /// <summary>1-based position among the entries (screen readers announce "3 of 12"); 0 for non-entries.</summary>
    public int Position
    {
        get => _position;
        set
        {
            if (_position != value)
            {
                _position = value;
                OnPropertyChanged(nameof(Position));
            }
        }
    }

    public int SetSize
    {
        get => _setSize;
        set
        {
            if (_setSize != value)
            {
                _setSize = value;
                OnPropertyChanged(nameof(SetSize));
            }
        }
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class HeaderRow : PopupRow
{
    public HeaderRow(string title, int count)
    {
        Title = title;
        Count = count;
    }

    public string Title { get; }

    public int Count { get; }

    public string Display => Count > 0 ? $"{Title} ({Count})" : Title;

    public override string AutomationName => Display;
}

public sealed class NoticeRow : PopupRow
{
    public NoticeRow(string text, string glyph, bool isWarning)
    {
        Text = text;
        Glyph = glyph;
        IsWarning = isWarning;
    }

    public string Text { get; }

    public string Glyph { get; }

    public bool IsWarning { get; }

    public override string AutomationName => Text;
}

public sealed class MoreRow : PopupRow
{
    public MoreRow(string text) => Text = text;

    public string Text { get; }

    public override bool IsSelectable => true;

    public override string AutomationName => Text;
}

/// <summary>
/// One history entry. Reused across rebuilds while its entry is unchanged (see
/// <see cref="Refresh"/>), so the list keeps its containers while the user types.
/// </summary>
public sealed class EntryRow : PopupRow
{
    public const string GlyphText = "";
    public const string GlyphLink = "";
    public const string GlyphFolder = "";
    public const string GlyphPin = "";

    private bool _isCurrent;

    public EntryRow(HistoryEntry entry, string[] terms, DateTimeOffset now)
    {
        Entry = entry;
        Kind = ContentClassifier.Classify(entry.SearchText);
        LineInfo = entry.LineCount > 1 ? Strings.LinesShort(entry.LineCount) : string.Empty;
        (Terms, Preview, Time) = Compute(terms, now);
    }

    public HistoryEntry Entry { get; }

    public string[] Terms { get; private set; }

    public ContentKind Kind { get; }

    public string Preview { get; private set; }

    public string Time { get; private set; }

    /// <summary>New search terms or a later "now": updates only what changed.</summary>
    public void Refresh(string[] terms, DateTimeOffset now)
    {
        var (newTerms, preview, time) = Compute(terms, now);
        var changed = false;
        if (!Terms.AsSpan().SequenceEqual(newTerms))
        {
            Terms = newTerms;
            OnPropertyChanged(nameof(Terms));
        }

        if (preview != Preview)
        {
            Preview = preview;
            OnPropertyChanged(nameof(Preview));
            changed = true;
        }

        if (time != Time)
        {
            Time = time;
            OnPropertyChanged(nameof(Time));
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    private (string[] Terms, string Preview, string Time) Compute(string[] terms, DateTimeOffset now)
    {
        var match = terms.Length == 0 ? -1 : Core.Search.HistorySearch.FirstMatchIndex(Entry.SearchText, terms);
        var preview = match > 40 ? PreviewText.Snippet(Entry.SearchText, match) : PreviewText.ForRow(Entry.SearchText);
        var used = DateTimeOffset.FromUnixTimeMilliseconds(Entry.LastUsedAtMs);
        return (terms, preview, Strings.RelativeTime(used, now));
    }

    public string LineInfo { get; }

    public bool IsPinned => Entry.IsPinned;

    public string Glyph => IsPinned ? GlyphPin : Kind switch
    {
        ContentKind.Url => GlyphLink,
        ContentKind.Path => GlyphFolder,
        _ => GlyphText,
    };

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                OnPropertyChanged(nameof(IsCurrent));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public override bool IsSelectable => true;

    public override string AutomationName
    {
        get
        {
            var parts = new List<string> { Preview };
            if (IsPinned)
            {
                parts.Add(Strings.PinnedHeader.ToLower(Strings.Culture));
            }

            if (IsCurrent)
            {
                parts.Add(Strings.CurrentBadge.ToLower(Strings.Culture));
            }

            if (LineInfo.Length > 0)
            {
                parts.Add(Strings.Lines(Entry.LineCount));
            }

            parts.Add(Time);
            return string.Join(", ", parts);
        }
    }
}

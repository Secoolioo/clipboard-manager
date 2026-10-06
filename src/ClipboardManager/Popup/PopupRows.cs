using System.ComponentModel;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Text;
using ClipboardManager.Localization;

namespace ClipboardManager.Popup;

/// <summary>One line of the popup list. Only entries and the "more" toggle are selectable.</summary>
public abstract class PopupRow
{
    public virtual bool IsSelectable => false;

    public virtual string AutomationName => string.Empty;
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

public sealed class EntryRow : PopupRow, INotifyPropertyChanged
{
    public const string GlyphText = "";
    public const string GlyphLink = "";
    public const string GlyphFolder = "";
    public const string GlyphPin = "";

    private bool _isCurrent;

    public EntryRow(HistoryEntry entry, string[] terms, DateTimeOffset now)
    {
        Entry = entry;
        Terms = terms;
        Kind = ContentClassifier.Classify(entry.SearchText);
        var match = terms.Length == 0 ? -1 : Core.Search.HistorySearch.FirstMatchIndex(entry.SearchText, terms);
        Preview = match > 40 ? PreviewText.Snippet(entry.SearchText, match) : PreviewText.ForRow(entry.SearchText);
        var used = DateTimeOffset.FromUnixTimeMilliseconds(entry.LastUsedAtMs);
        Time = Strings.RelativeTime(used, now);
        LineInfo = entry.LineCount > 1 ? Strings.LinesShort(entry.LineCount) : string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryEntry Entry { get; }

    public string[] Terms { get; }

    public ContentKind Kind { get; }

    public string Preview { get; }

    public string Time { get; }

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
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

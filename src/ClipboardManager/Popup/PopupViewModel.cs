using System.Text;
using System.Windows.Media;
using ClipboardManager.Common;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Search;
using ClipboardManager.Localization;

namespace ClipboardManager.Popup;

/// <summary>
/// State of one popup session. It works on a snapshot taken when the popup opens, so the list never
/// shifts under the cursor because something was captured in the background.
/// </summary>
public sealed class PopupViewModel : ObservableObject
{
    public const int CollapsedPinnedCount = 3;
    public const int PreviewMaxLines = 60;

    private HistorySnapshot _source = HistorySnapshot.Empty;
    private string _query = string.Empty;
    private readonly RowCollection _rows = [];
    private readonly Dictionary<long, EntryRow> _entryRows = [];
    private readonly Dictionary<string, PopupRow> _fixedRows = [];
    private PopupRow? _selected;
    private long? _currentEntryId;
    private SkipReason _skip;
    private bool _isLoaded = true;
    private bool _pinnedExpanded;
    private bool _isCompact;
    private string _previewText = string.Empty;
    private string _previewMeta = string.Empty;
    private bool _previewMonospace;
    private bool _previewTruncated;
    private bool _previewPartial;
    private ImageSource? _previewImage;
    private double _previewImageWidth;
    private double _previewImageHeight;
    private HistoryEntry? _imageWanted;
    private bool _imageLoading;
    private string? _inlineMessage;
    private string _statusText = Strings.StatusActive;
    private bool _isPaused;
    private string _pauseBanner = string.Empty;
    private bool _hotkeyMissing;
    private DeletedEntry? _pendingUndo;
    private string _queryAtDelete = string.Empty;
    private string _resultsText = string.Empty;

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? string.Empty))
            {
                InlineMessage = null;
                Rebuild(selectDefault: true);
            }
        }
    }

    /// <summary>
    /// One collection for the window's lifetime, updated in place: the list then reuses (recycles)
    /// its item containers while typing instead of rebuilding all of them for a new list.
    /// </summary>
    public IReadOnlyList<PopupRow> Rows => _rows;

    public PopupRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                UpdatePreview();
            }
        }
    }

    public EntryRow? SelectedEntry => _selected as EntryRow;

    public bool HasEntries => _source.Count > 0;

    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            // Back from compact: a picture that was skipped while the preview was hidden is loaded now.
            if (Set(ref _isCompact, value) && !value)
            {
                RequestMissingImage();
            }
        }
    }

    public bool PinnedExpanded => _pinnedExpanded;

    public string PreviewText
    {
        get => _previewText;
        private set => Set(ref _previewText, value);
    }

    public string PreviewMeta
    {
        get => _previewMeta;
        private set => Set(ref _previewMeta, value);
    }

    public bool PreviewMonospace
    {
        get => _previewMonospace;
        private set => Set(ref _previewMonospace, value);
    }

    public bool PreviewTruncated
    {
        get => _previewTruncated;
        private set => Set(ref _previewTruncated, value);
    }

    public bool PreviewPartiallySearchable
    {
        get => _previewPartial;
        private set => Set(ref _previewPartial, value);
    }

    /// <summary>For an image entry: its thumbnail at once, then the picture itself (see <see cref="ImageLoader"/>).</summary>
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set => Set(ref _previewImage, value);
    }

    /// <summary>The picture's size in pixels: the thumbnail and the full picture fill the same box, nothing jumps.</summary>
    public double PreviewImageWidth
    {
        get => _previewImageWidth;
        private set => Set(ref _previewImageWidth, value);
    }

    public double PreviewImageHeight
    {
        get => _previewImageHeight;
        private set => Set(ref _previewImageHeight, value);
    }

    /// <summary>Loads the picture of an image entry for the preview pane; returns null when there is nothing to show.</summary>
    public Func<HistoryEntry, Task<ImageSource?>>? ImageLoader { get; set; }

    public string? InlineMessage
    {
        get => _inlineMessage;
        set => Set(ref _inlineMessage, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => Set(ref _isPaused, value);
    }

    public string PauseBanner
    {
        get => _pauseBanner;
        private set => Set(ref _pauseBanner, value);
    }

    public bool HotkeyMissing
    {
        get => _hotkeyMissing;
        set => Set(ref _hotkeyMissing, value);
    }

    public bool CanUndo => _pendingUndo is not null && _query == _queryAtDelete;

    /// <summary>"5 matches" while searching (announced politely to screen readers), otherwise empty.</summary>
    public string ResultsText
    {
        get => _resultsText;
        private set => Set(ref _resultsText, value);
    }

    public void Load(HistorySnapshot snapshot, long? currentEntryId, SkipReason skip, bool isLoaded, bool pinnedExpanded)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _source = snapshot;
        _currentEntryId = currentEntryId;
        _skip = skip;
        _isLoaded = isLoaded;
        _pinnedExpanded = pinnedExpanded;
        _pendingUndo = null;
        InlineMessage = null;
        if (_query.Length > 0)
        {
            _query = string.Empty;
            OnPropertyChanged(nameof(Query));
        }

        Rebuild(selectDefault: true);

        // A warm-up that a real open took over keeps its selection, so the picture it skipped loads now.
        RequestMissingImage();
    }

    public void SetStatus(string statusText, bool paused, DateTimeOffset? pausedUntil)
    {
        StatusText = statusText;
        IsPaused = paused;
        PauseBanner = paused ? Strings.PauseBanner(pausedUntil) : string.Empty;
    }

    /// <summary>Called right before the window hides, so the next show starts clean without layout work.</summary>
    public void Reset()
    {
        _pendingUndo = null;
        _imageWanted = null;
        InlineMessage = null;
        _source = HistorySnapshot.Empty;
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        _rows.Clear();
        _entryRows.Clear();
        _fixedRows.Clear();
        Selected = null;
    }

    public void MoveSelection(int delta)
    {
        var selectable = _rows.Where(r => r.IsSelectable).ToList();
        if (selectable.Count == 0)
        {
            return;
        }

        var index = _selected is null ? -1 : selectable.IndexOf(_selected);
        var next = index < 0 ? (delta > 0 ? 0 : selectable.Count - 1) : Math.Clamp(index + delta, 0, selectable.Count - 1);
        Selected = selectable[next];
    }

    public void SelectFirst() => Selected = _rows.FirstOrDefault(r => r.IsSelectable);

    public void SelectLast() => Selected = _rows.LastOrDefault(r => r.IsSelectable);

    public void TogglePinnedExpanded()
    {
        _pinnedExpanded = !_pinnedExpanded;
        var keep = _selected is MoreRow ? null : (_selected as EntryRow)?.Entry.Id;
        Rebuild(selectDefault: false);
        Selected = keep is null
            ? _rows.OfType<MoreRow>().FirstOrDefault() ?? _rows.FirstOrDefault(r => r.IsSelectable)
            : _rows.OfType<EntryRow>().FirstOrDefault(r => r.Entry.Id == keep);
    }

    /// <summary>Mirrors a pin/unpin into the session snapshot.</summary>
    public void ApplyUpsert(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var pinned = _source.Pinned.Where(e => e.Id != entry.Id).ToList();
        var history = _source.History.Where(e => e.Id != entry.Id).ToList();
        if (entry.IsPinned)
        {
            pinned.Insert(0, entry);
        }
        else
        {
            history.Add(entry);
            history.Sort(static (a, b) => b.LastUsedAtMs.CompareTo(a.LastUsedAtMs) is var c and not 0 ? c : b.Id.CompareTo(a.Id));
        }

        _source = new HistorySnapshot(pinned, history);
        Rebuild(selectDefault: false);
        Selected = _rows.OfType<EntryRow>().FirstOrDefault(r => r.Entry.Id == entry.Id) ?? _rows.FirstOrDefault(r => r.IsSelectable);
    }

    /// <summary>Removes a deleted entry from the snapshot and remembers it for Ctrl+Z.</summary>
    public void ApplyDeletion(DeletedEntry deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        var selectable = _rows.Where(r => r.IsSelectable).ToList();
        var index = _selected is null ? 0 : Math.Max(0, selectable.IndexOf(_selected));

        _source = new HistorySnapshot(
            _source.Pinned.Where(e => e.Id != deleted.Entry.Id).ToList(),
            _source.History.Where(e => e.Id != deleted.Entry.Id).ToList());
        _pendingUndo = deleted;
        _queryAtDelete = _query;
        Rebuild(selectDefault: false);
        var remaining = _rows.Where(r => r.IsSelectable).ToList();
        Selected = remaining.Count == 0 ? null : remaining[Math.Min(index, remaining.Count - 1)];
        InlineMessage = Strings.Deleted;
    }

    public DeletedEntry? TakeUndo()
    {
        if (!CanUndo)
        {
            return null;
        }

        var undo = _pendingUndo;
        _pendingUndo = null;
        InlineMessage = null;
        return undo;
    }

    private void Rebuild(bool selectDefault)
    {
        var terms = HistorySearch.ParseTerms(_query);
        var filtered = HistorySearch.Filter(_source, _query, Strings.ImageWord);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<PopupRow>(filtered.Count + 4);

        if (!_isLoaded)
        {
            rows.Add(Fixed(new NoticeRow(Strings.Loading, "", isWarning: false)));
        }

        if (_skip != SkipReason.None && terms.Length == 0)
        {
            rows.Add(Fixed(new NoticeRow(Strings.Skipped(_skip), "", isWarning: true)));
        }

        if (filtered.Pinned.Count > 0)
        {
            rows.Add(Fixed(new HeaderRow(Strings.PinnedHeader, filtered.Pinned.Count)));
            var showAll = terms.Length > 0 || _pinnedExpanded || filtered.Pinned.Count <= CollapsedPinnedCount + 1;
            var shown = showAll ? filtered.Pinned : filtered.Pinned.Take(CollapsedPinnedCount);
            foreach (var entry in shown)
            {
                rows.Add(Row(entry, terms, now));
            }

            if (!showAll)
            {
                rows.Add(Fixed(new MoreRow(Strings.MorePinned(filtered.Pinned.Count - CollapsedPinnedCount))));
            }
            else if (_pinnedExpanded && terms.Length == 0 && filtered.Pinned.Count > CollapsedPinnedCount + 1)
            {
                rows.Add(Fixed(new MoreRow(Strings.FewerPinned)));
            }

            if (filtered.History.Count > 0)
            {
                rows.Add(Fixed(new HeaderRow(Strings.HistoryHeader, filtered.History.Count)));
            }
        }

        foreach (var entry in filtered.History)
        {
            rows.Add(Row(entry, terms, now));
        }

        if (filtered.Count == 0 && _isLoaded)
        {
            rows.Add(Fixed(terms.Length > 0
                ? new NoticeRow(Strings.NoResults, "", isWarning: false)
                : new NoticeRow(Strings.EmptyHint, "", isWarning: false)));
        }

        var entryRows = rows.OfType<EntryRow>().ToList();
        for (var i = 0; i < entryRows.Count; i++)
        {
            entryRows[i].Position = i + 1;
            entryRows[i].SetSize = entryRows.Count;
        }

        _rows.Update(rows);
        ResultsText = terms.Length > 0 ? Strings.Matches(filtered.Count) : string.Empty;
        OnPropertyChanged(nameof(HasEntries));
        if (selectDefault)
        {
            Selected = DefaultSelection(rows, terms.Length > 0);
        }
        else if (_selected is not null && !rows.Contains(_selected))
        {
            Selected = null;
        }
    }

    /// <summary>The same header, notice or "more" row while its text stays the same (see <see cref="Row"/>).</summary>
    private PopupRow Fixed(PopupRow row)
    {
        var key = row.GetType().Name + "|" + row.AutomationName;
        if (_fixedRows.TryGetValue(key, out var existing))
        {
            return existing;
        }

        _fixedRows[key] = row;
        return row;
    }

    /// <summary>The same row object for an unchanged entry, so typing does not rebuild the list's containers.</summary>
    private EntryRow Row(HistoryEntry entry, string[] terms, DateTimeOffset now)
    {
        if (_entryRows.TryGetValue(entry.Id, out var row) && ReferenceEquals(row.Entry, entry))
        {
            row.Refresh(terms, now);
        }
        else
        {
            row = new EntryRow(entry, terms, now);
            _entryRows[entry.Id] = row;
        }

        row.IsCurrent = entry.Id == _currentEntryId;
        return row;
    }

    /// <summary>
    /// Without a query: the newest history entry that is not already in the clipboard (Alt+Tab logic,
    /// so "hotkey, Enter" pastes the previous clip). With a query: the first history match.
    /// </summary>
    private static PopupRow? DefaultSelection(List<PopupRow> rows, bool searching)
    {
        var history = rows.OfType<EntryRow>().Where(r => !r.IsPinned).ToList();
        if (!searching)
        {
            var candidate = history.FirstOrDefault(r => !r.IsCurrent);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return (PopupRow?)history.FirstOrDefault() ?? rows.FirstOrDefault(r => r.IsSelectable);
    }

    private void UpdatePreview()
    {
        _imageWanted = null;
        if (_selected is not EntryRow row)
        {
            PreviewText = string.Empty;
            PreviewMeta = string.Empty;
            PreviewTruncated = false;
            PreviewPartiallySearchable = false;
            PreviewImage = null;
            return;
        }

        var entry = row.Entry;
        if (entry.Image is { } image)
        {
            PreviewText = string.Empty;
            PreviewTruncated = false;
            PreviewPartiallySearchable = false;
            PreviewMonospace = false;
            PreviewImageWidth = image.Width;
            PreviewImageHeight = image.Height;
            PreviewImage = row.Thumbnail;
            PreviewMeta = Meta(Strings.ImageSize(image.Width, image.Height, image.ByteCount), entry);
            if (!_isCompact)
            {
                _imageWanted = entry;
                _ = LoadImageAsync();
            }

            return;
        }

        PreviewImage = null;
        var text = entry.SearchText;
        var lines = 0;
        var cut = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && ++lines >= PreviewMaxLines)
            {
                cut = i;
                break;
            }
        }

        PreviewText = cut < text.Length ? text[..cut] : text;
        PreviewTruncated = cut < text.Length || entry.IsPartiallySearchable;
        PreviewPartiallySearchable = entry.IsPartiallySearchable;
        PreviewMonospace = entry.LineCount > 1;

        PreviewMeta = Meta(Strings.Characters(entry.CharCount) + " · " + Strings.Lines(Math.Max(1, entry.LineCount)), entry);
    }

    private static string Meta(string size, HistoryEntry entry)
    {
        var meta = new StringBuilder()
            .Append(size)
            .AppendLine()
            .Append(Strings.CopiedAt(Strings.AbsoluteTime(DateTimeOffset.FromUnixTimeMilliseconds(entry.LastUsedAtMs))));
        if (entry.PinnedAtMs is { } pinned)
        {
            meta.AppendLine().Append(Strings.PinnedAt(Strings.AbsoluteTime(DateTimeOffset.FromUnixTimeMilliseconds(pinned))));
        }

        return meta.ToString();
    }

    /// <summary>Loads the picture of the selected image entry if only its thumbnail is shown and nothing is loading.</summary>
    private void RequestMissingImage()
    {
        if (!_isCompact && _imageWanted is null && _selected is EntryRow { IsImage: true } row && ReferenceEquals(PreviewImage, row.Thumbnail))
        {
            _imageWanted = row.Entry;
            _ = LoadImageAsync();
        }
    }

    /// <summary>
    /// One picture at a time: arrowing through the list does not queue a decode per row. When a load
    /// ends, the entry selected by then is loaded next.
    /// </summary>
    private async Task LoadImageAsync()
    {
        if (_imageLoading)
        {
            return;
        }

        _imageLoading = true;
        try
        {
            while (_imageWanted is { } wanted && ImageLoader is { } load)
            {
                var image = await load(wanted).ConfigureAwait(true);
                if (!ReferenceEquals(_imageWanted, wanted))
                {
                    continue;
                }

                _imageWanted = null;
                if (image is not null && ReferenceEquals(SelectedEntry?.Entry, wanted))
                {
                    PreviewImage = image;
                }
            }
        }
        finally
        {
            _imageLoading = false;
        }
    }
}

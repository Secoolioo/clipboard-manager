using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ClipboardManager.Popup;

/// <summary>
/// The popup's rows, updated in place. Typing mostly narrows or widens the same ordered list, so
/// removing and inserting just the rows that changed lets the list keep (recycle) its item
/// containers. Large or reordering changes become one reset instead of thousands of events.
/// </summary>
public sealed class RowCollection : ObservableCollection<PopupRow>
{
    /// <summary>Above this many single changes one reset is cheaper.</summary>
    private const int MaxIncrementalChanges = 1000;

    public void Update(IReadOnlyList<PopupRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var wanted = new HashSet<PopupRow>(rows, ReferenceEqualityComparer.Instance);
        var kept = 0;
        foreach (var row in Items)
        {
            if (wanted.Contains(row))
            {
                kept++;
            }
        }

        if ((Count - kept) + (rows.Count - kept) > MaxIncrementalChanges || !KeptRowsAreInOrder(rows, wanted))
        {
            ResetTo(rows);
            return;
        }

        for (var i = Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Items[i]))
            {
                RemoveAt(i);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Count || !ReferenceEquals(Items[i], rows[i]))
            {
                Insert(i, rows[i]);
            }
        }
    }

    /// <summary>The rows that stay must keep their relative order, or inserting around them would not converge.</summary>
    private bool KeptRowsAreInOrder(IReadOnlyList<PopupRow> rows, HashSet<PopupRow> wanted)
    {
        var next = 0;
        foreach (var row in Items)
        {
            if (!wanted.Contains(row))
            {
                continue;
            }

            while (next < rows.Count && !ReferenceEquals(rows[next], row))
            {
                next++;
            }

            if (next == rows.Count)
            {
                return false;
            }

            next++;
        }

        return true;
    }

    private void ResetTo(IReadOnlyList<PopupRow> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

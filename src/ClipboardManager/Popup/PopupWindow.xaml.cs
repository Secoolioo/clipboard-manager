using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipboardManager.Interop;
using ClipboardManager.Localization;

namespace ClipboardManager.Popup;

/// <summary>Actions the popup asks its controller to perform.</summary>
internal interface IPopupActions
{
    Task CopyAsync(EntryRow row);

    Task TogglePinAsync(EntryRow row);

    Task DeleteAsync(EntryRow row);

    Task UndoAsync();

    void Close(bool restoreFocus);

    void OpenSettings();

    void Resume();
}

/// <summary>
/// The history popup. Created once and reused: show/hide only, never closed (Alt+F4 hides it),
/// so opening never pays for window creation.
/// </summary>
internal sealed partial class PopupWindow : Window
{
    public const double FullWidth = 780;
    public const double CompactWidth = 420;
    public const double BaseHeight = 480;

    private const int ClickGuardMilliseconds = 150;

    private readonly System.Windows.Threading.DispatcherTimer _announceResults;
    private readonly List<FrameworkElement> _secondaryHints = [];
    private long _shownAt;
    private bool _allowClose;

    public PopupWindow(PopupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _announceResults = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _announceResults.Tick += (_, _) =>
        {
            _announceResults.Stop();
            Announce(ResultsLive);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _announceResults.Stop();
            }
        };
        BuildHints();
        ApplyCompact(viewModel.IsCompact);
        viewModel.PropertyChanged += OnViewModelChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        Deactivated += (_, _) =>
        {
            if (IsVisible && !IsPrewarming)
            {
                Actions?.Close(restoreFocus: false);
            }
        };
    }

    public PopupViewModel ViewModel { get; }

    public IPopupActions? Actions { get; set; }

    public bool IsPrewarming { get; set; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public double Scale { get; private set; } = 1;

    public void ApplyScale(double scale)
    {
        Scale = scale;
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    public void ApplyCompact(bool compact)
    {
        PreviewColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ListColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(400);
        PreviewPane.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Divider.Visibility = PreviewPane.Visibility;

        // The narrow window has room for one line of hints: keep copy, preview toggle and close.
        foreach (var hint in _secondaryHints)
        {
            hint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Logical (DIP) size before text scaling.</summary>
    public Size LogicalSize => new(ViewModel.IsCompact ? CompactWidth : FullWidth, BaseHeight);

    public void SetStatusVisual(bool paused, bool warning)
    {
        StatusGlyph.Text = paused ? "" : warning ? "" : "";
        StatusGlyph.SetResourceReference(TextBlock.ForegroundProperty, paused || warning ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
    }

    /// <summary>True once the user pressed a key, typed or clicked in this session (stops background refreshes).</summary>
    public bool HasUserInput { get; private set; }

    public void MarkShown()
    {
        _shownAt = Environment.TickCount64;
        HasUserInput = false;
        Search.Focus();
        Keyboard.Focus(Search);
        Search.CaretIndex = Search.Text.Length;
        if (ViewModel.Selected is { } selected)
        {
            List.ScrollIntoView(selected);
        }
    }

    public void AllowCloseForShutdown() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 must not destroy the reusable window (and must never end the app).
        if (!_allowClose)
        {
            e.Cancel = true;
            Actions?.Close(restoreFocus: true);
        }

        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = Handle;
        Dwm.Set(hwnd, Dwm.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        Dwm.Set(hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_ROUND);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PopupViewModel.Selected) when ViewModel.Selected is { } selected:
                List.ScrollIntoView(selected);
                break;
            case nameof(PopupViewModel.IsCompact):
                ApplyCompact(ViewModel.IsCompact);
                break;
            case nameof(PopupViewModel.ResultsText) when IsVisible:
                // Debounced so a screen reader is not interrupted on every keystroke.
                _announceResults.Stop();
                _announceResults.Start();
                break;
            case nameof(PopupViewModel.InlineMessage) when IsVisible && ViewModel.InlineMessage is not null:
                Announce(InlineMessageText);
                break;
        }
    }

    private static void Announce(UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        HasUserInput = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var searchHasSelection = Search.SelectionLength > 0 && Search.IsKeyboardFocusWithin;
        var row = ViewModel.Selected;

        switch (key)
        {
            case Key.Escape:
                Actions?.Close(restoreFocus: true);
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                Activate(row);
                break;
            case Key.Enter when modifiers == ModifierKeys.Shift:
                // Reserved for "copy and paste" (planned), so Enter never changes meaning later.
                break;
            case Key.Up:
                Move(-1);
                break;
            case Key.Down:
                Move(1);
                break;
            case Key.PageUp:
                Move(-8);
                break;
            case Key.PageDown:
                Move(8);
                break;
            case Key.Home when modifiers == ModifierKeys.Control:
                ViewModel.SelectFirst();
                FocusSelected();
                break;
            case Key.End when modifiers == ModifierKeys.Control:
                ViewModel.SelectLast();
                FocusSelected();
                break;
            case Key.Tab:
                ViewModel.IsCompact = !ViewModel.IsCompact;
                break;
            case Key.P when modifiers == ModifierKeys.Control && row is EntryRow pinRow:
                _ = Actions?.TogglePinAsync(pinRow);
                break;
            case Key.Delete when modifiers == ModifierKeys.Shift && !searchHasSelection && row is EntryRow deleteRow:
                _ = Actions?.DeleteAsync(deleteRow);
                break;
            case Key.Z when modifiers == ModifierKeys.Control && ViewModel.CanUndo:
                _ = Actions?.UndoAsync();
                break;
            case Key.C when modifiers == ModifierKeys.Control && !searchHasSelection && row is EntryRow copyRow:
                _ = Actions?.CopyAsync(copyRow);
                break;
            case Key.R when modifiers == ModifierKeys.Control && ViewModel.IsPaused:
                Actions?.Resume();
                break;
            case Key.Apps:
            case Key.F10 when modifiers == ModifierKeys.Shift:
                OpenContextMenu(row as EntryRow, null);
                break;
            case Key.Back when !Search.IsKeyboardFocusWithin:
                if (ViewModel.Query.Length > 0)
                {
                    ViewModel.Query = ViewModel.Query[..^1];
                }

                FocusSearch();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        HasUserInput = true;
        // Typing while a list item has focus (screen-reader navigation) still filters.
        if (!Search.IsKeyboardFocusWithin && !string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
        {
            ViewModel.Query += e.Text;
            FocusSearch();
            e.Handled = true;
        }
    }

    private void Move(int delta)
    {
        ViewModel.MoveSelection(delta);
        FocusSelected();
    }

    /// <summary>Real keyboard focus on the selected item, so screen readers announce it.</summary>
    private void FocusSelected()
    {
        if (ViewModel.Selected is not { } selected)
        {
            return;
        }

        List.ScrollIntoView(selected);
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
        {
            item.Focus();
        }
    }

    private void FocusSearch()
    {
        Search.Focus();
        Search.CaretIndex = Search.Text.Length;
    }

    private void Activate(PopupRow? row)
    {
        switch (row)
        {
            case EntryRow entry:
                _ = Actions?.CopyAsync(entry);
                break;
            case MoreRow:
                ViewModel.TogglePinnedExpanded();
                FocusSelected();
                break;
        }
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        HasUserInput = true;
        if (Environment.TickCount64 - _shownAt < ClickGuardMilliseconds)
        {
            e.Handled = true;
            return;
        }

        if (sender is ListBoxItem { DataContext: PopupRow row })
        {
            ViewModel.Selected = row;
            Activate(row);
            e.Handled = true;
        }
    }

    private void OnItemRightDown(object sender, MouseButtonEventArgs e)
    {
        HasUserInput = true;
        if (sender is ListBoxItem { DataContext: EntryRow row } item)
        {
            ViewModel.Selected = row;
            OpenContextMenu(row, item);
            e.Handled = true;
        }
    }

    private void OpenContextMenu(EntryRow? row, ListBoxItem? anchor)
    {
        if (row is null)
        {
            return;
        }

        anchor ??= List.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        var menu = new ContextMenu { PlacementTarget = anchor ?? (UIElement)List };
        menu.Items.Add(Item(Strings.MenuCopy, "Enter", () => _ = Actions?.CopyAsync(row)));
        menu.Items.Add(Item(row.IsPinned ? Strings.MenuUnpin : Strings.MenuPin, Strings.IsGerman ? "Strg+P" : "Ctrl+P", () => _ = Actions?.TogglePinAsync(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Strings.MenuDelete, Strings.IsGerman ? "Umschalt+Entf" : "Shift+Del", () => _ = Actions?.DeleteAsync(row)));
        menu.Closed += (_, _) => FocusSearch();
        menu.IsOpen = true;

        static MenuItem Item(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Actions?.OpenSettings();

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        Actions?.Resume();
        FocusSearch();
    }

    private void BuildHints()
    {
        var german = Strings.IsGerman;
        (string Key, string Label, bool Secondary)[] hints =
        [
            ("↵", Strings.HintCopy, false),
            (german ? "⇧ Entf" : "⇧ Del", Strings.HintDelete, true),
            (german ? "Strg P" : "Ctrl P", Strings.HintPin, true),
            ("Tab", Strings.HintPreview, false),
            ("Esc", Strings.HintClose, false),
        ];

        foreach (var (key, label, secondary) in hints)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            var border = new Border { Style = (Style)FindResource("Kbd"), Child = new TextBlock { Text = key, FontSize = 11 } };
            panel.Children.Add(border);
            panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("HintText") });
            Hints.Children.Add(panel);
            if (secondary)
            {
                _secondaryHints.Add(panel);
            }
        }
    }
}

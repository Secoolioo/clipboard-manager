using System.Windows;
using ClipboardManager.Common;
using ClipboardManager.Localization;

namespace ClipboardManager.Dialogs;

/// <summary>Themed confirmation for destructive actions. The default button is always Cancel.</summary>
internal sealed partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirm, string? option)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Message.Text = message;
        ConfirmButton.Content = confirm;
        CancelButton.Content = Strings.Cancel;
        if (option is not null)
        {
            Option.Content = option;
            Option.Visibility = Visibility.Visible;
        }

        SourceInitialized += (_, _) => ThemeHelper.RoundCorners(this);
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>Returns null when cancelled, otherwise the state of the optional checkbox.</summary>
    public static bool? Ask(string title, string message, string confirm, string? option = null, Window? owner = null)
    {
        var dialog = new ConfirmDialog(title, message, confirm, option);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.Activate();
        return dialog.ShowDialog() == true ? dialog.Option.IsChecked == true : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

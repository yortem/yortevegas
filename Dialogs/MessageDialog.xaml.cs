using System.Windows;
using YorteVegas.Services;

namespace YorteVegas.Dialogs;

public enum MessageResult
{
    None,
    Primary,
    Secondary,
    Cancel
}

/// <summary>Small dark replacement for MessageBox so prompts match the editor.</summary>
public partial class MessageDialog : Window
{
    private MessageResult _result = MessageResult.None;

    private MessageDialog(string title, string message, string primary, string? secondary, string? cancel)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;

        if (string.IsNullOrEmpty(secondary))
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondary;
        }

        if (string.IsNullOrEmpty(cancel))
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancel;
        }

        Loaded += (_, _) => PrimaryButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Finish(MessageResult.Cancel);
                e.Handled = true;
            }
        };
    }

    public static MessageResult Show(Window owner, string title, string message,
        string primary, string? secondary = null, string? cancel = "Cancel")
    {
        var dialog = new MessageDialog(title, message, primary, secondary, cancel) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    public static void ShowInfo(Window owner, string title, string message) =>
        Show(owner, title, message, "OK", null, null);

    private void Primary_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.Primary);

    private void Secondary_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.Secondary);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.Cancel);

    private void Finish(MessageResult result)
    {
        _result = result;
        DialogResult = true;
    }
}

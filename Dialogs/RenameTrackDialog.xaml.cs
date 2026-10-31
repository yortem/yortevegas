using System.Windows;
using YorteVegas.Services;

namespace YorteVegas.Dialogs;

public partial class RenameTrackDialog : Window
{
    public string TrackName { get; private set; } = "";

    public RenameTrackDialog(string currentName)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.Focus();
            return;
        }

        TrackName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

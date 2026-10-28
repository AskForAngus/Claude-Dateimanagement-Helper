using System.Windows;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public enum DialogChoice { None, Primary, Secondary, Tertiary }

public partial class MessageDialog : FluentWindow
{
    public MessageDialog(string title, string message, string primary, string? secondary, string? tertiary = null,
        string? details = null, bool danger = false, SymbolRegular? icon = null)
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        Title = title;
        TitleBar.Title = title;
        Heading.Text = title;
        Message.Text = message;
        PrimaryButton.Content = primary;
        if (danger) PrimaryButton.Appearance = ControlAppearance.Danger;

        if (secondary == null) SecondaryButton.Visibility = Visibility.Collapsed;
        else SecondaryButton.Content = secondary;

        if (tertiary != null)
        {
            TertiaryButton.Content = tertiary;
            TertiaryButton.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(details))
        {
            Details.Text = details;
            Details.Visibility = Visibility.Visible;
        }

        if (icon is { } s)
        {
            HeadingIcon.Symbol = s;
            HeadingIcon.Visibility = Visibility.Visible;
        }
    }

    public DialogChoice Choice { get; private set; }

    private void Primary_Click(object sender, RoutedEventArgs e) { Choice = DialogChoice.Primary; DialogResult = true; }
    private void Secondary_Click(object sender, RoutedEventArgs e) { Choice = DialogChoice.Secondary; DialogResult = false; }
    private void Tertiary_Click(object sender, RoutedEventArgs e) { Choice = DialogChoice.Tertiary; DialogResult = true; }
}

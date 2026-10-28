using System.Windows;
using NasZentrale.Core.TrueNas;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public partial class ShareEditDialog : FluentWindow
{
    private readonly SmbShare? _existing;

    public ShareEditDialog(SmbShare? existing, string? presetPath = null)
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        _existing = existing;
        var title = existing == null ? "Neue Freigabe" : $"Freigabe „{existing.Name}“ bearbeiten";
        Title = TitleBar.Title = Heading.Text = title;
        OkButton.Content = existing == null ? "Anlegen" : "Speichern";

        // ABE/Schreibschutz nur anbieten, wenn diese TrueNAS-Version die Felder kennt.
        var shares = Session.Current.Shares;
        var abeSupported = existing?.AccessBasedEnumeration != null || (existing == null && shares.Any(s => s.AccessBasedEnumeration != null));
        var roSupported = existing?.ReadOnly != null || (existing == null && shares.Any(s => s.ReadOnly != null));
        AbeBox.Visibility = abeSupported ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyBox.Visibility = roSupported ? Visibility.Visible : Visibility.Collapsed;

        if (existing != null)
        {
            NameBox.Text = existing.Name;
            PathBox.Text = existing.Path;
            CommentBox.Text = existing.Comment;
            EnabledSwitch.IsChecked = existing.Enabled;
            AbeBox.IsChecked = existing.AccessBasedEnumeration == true;
            ReadOnlyBox.IsChecked = existing.ReadOnly == true;
        }
        if (existing == null && presetPath != null)
        {
            PathBox.Text = presetPath;
            NameBox.Text = Core.Paths.NasPath.Name(presetPath);
        }
        Loaded += (_, _) => NameBox.Focus();
        UpdatePreview();
    }

    public string ShareName => NameBox.Text.Trim();
    public string SharePath => PathBox.Text.Trim();
    public string Comment => CommentBox.Text.Trim();
    public bool ShareEnabled => EnabledSwitch.IsChecked == true;
    public bool? Abe => AbeBox.Visibility == Visibility.Visible ? AbeBox.IsChecked == true : null;
    public bool? ReadOnly => ReadOnlyBox.Visibility == Visibility.Visible ? ReadOnlyBox.IsChecked == true : null;

    private void Field_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        UncPreview.Text = ShareName.Length == 0 ? "" : $@"Erreichbar unter \\{Session.Current.SmbHost}\{ShareName}";
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = Ui.Prepare(new FolderPickerDialog("Ordner für die Freigabe wählen", SharePath));
        if (picker.ShowDialog() == true && picker.SelectedPath != null)
        {
            PathBox.Text = picker.SelectedPath;
            if (ShareName.Length == 0) NameBox.Text = Core.Paths.NasPath.Name(picker.SelectedPath);
        }
    }

    private string? Validate()
    {
        if (ShareName.Length == 0) return "Bitte einen Namen eingeben.";
        if (ShareName.Length > 80) return "Der Name ist zu lang (max. 80 Zeichen).";
        if (ShareName.IndexOfAny("\\/[]:|<>+=;,?*\"".ToCharArray()) >= 0) return "Der Name darf diese Zeichen nicht enthalten: \\ / [ ] : | < > + = ; , ? * \"";
        if (Session.Current.Shares.Any(s => s.Id != _existing?.Id && s.Name.Equals(ShareName, StringComparison.OrdinalIgnoreCase)))
            return "Eine Freigabe mit diesem Namen gibt es bereits.";
        if (SharePath.Length == 0) return "Bitte einen Ordner auswählen.";
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var error = Validate();
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        Helpers.DialogSizing.Fit(this);
        if (error == null) DialogResult = true;
    }
}

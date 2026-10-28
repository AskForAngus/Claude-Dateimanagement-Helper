using System.Windows;
using NasZentrale.Core.Accounts;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

/// <summary>Ersteinrichtung: gemeinsamen Protokollordner auf dem NAS festlegen.</summary>
public partial class SetupDialog : FluentWindow
{
    private string? _folder;
    private Principal? _group;

    public SetupDialog()
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        var s = Session.Current;
        if (s.Settings.AdminGroup.Length > 0)
        {
            _group = s.Principals.FirstOrDefault(p => p.Kind == PrincipalKind.Group && p.Name == s.Settings.AdminGroup);
            GroupBox.Text = _group?.Name ?? "";
        }
        if (s.Settings.ProtocolFolder.Length > 0)
        {
            _folder = s.Settings.ProtocolFolder;
            FolderBox.Text = _folder;
        }
        Update();
    }

    private void Update() => OkButton.IsEnabled = _folder != null && _group != null;

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = Ui.Prepare(new FolderPickerDialog("Ordner für das Protokoll", _folder));
        if (picker.ShowDialog() != true || picker.SelectedPath == null) return;
        _folder = picker.SelectedPath;
        FolderBox.Text = _folder;
        Update();
    }

    private void PickGroup_Click(object sender, RoutedEventArgs e)
    {
        var picker = Ui.Prepare(new PrincipalPickerDialog("Admin-Gruppe wählen", includeSpecial: false, kind: PrincipalKind.Group));
        if (picker.ShowDialog() != true || picker.Selected == null) return;
        _group = picker.Selected;
        GroupBox.Text = _group.Name;
        Update();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Session.Current.Settings.SetupDone = true;
        Session.Current.Settings.Save();
        DialogResult = false;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_folder == null || _group == null) return;
        OkButton.IsEnabled = LaterButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        Helpers.DialogSizing.Fit(this);
        try
        {
            var folder = await ProtocolSetup.SetupAsync(_folder, _group, new Progress<string>(t => BusyText.Text = t));
            Ui.Toast($"Gemeinsames Protokoll eingerichtet: {folder}");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Ui.ShowError("Einrichtung fehlgeschlagen", ex);
            OkButton.IsEnabled = LaterButton.IsEnabled = true;
            BusyPanel.Visibility = Visibility.Collapsed;
        }
    }
}

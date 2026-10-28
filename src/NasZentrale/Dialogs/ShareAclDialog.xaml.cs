using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NasZentrale.Core.Accounts;
using NasZentrale.Core.TrueNas;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public sealed partial class ShareAclRow : ObservableObject
{
    public ShareAclRow(ShareAce ace)
    {
        Ace = ace;
        _permIndex = (int)ace.Permission;
        _denyIndex = ace.Deny ? 1 : 0;
    }

    public ShareAce Ace { get; }
    public string Who => Ace.Who;

    [ObservableProperty] private int _permIndex;
    [ObservableProperty] private int _denyIndex;

    public ShareAce ToAce() => Ace with { Permission = (SharePermission)PermIndex, Deny = DenyIndex == 1 };
}

/// <summary>Freigabeberechtigungen (Share-ACL) bearbeiten.</summary>
public partial class ShareAclDialog : FluentWindow
{
    private const string EveryoneSid = "S-1-1-0";
    private readonly ObservableCollection<ShareAclRow> _rows;

    public ShareAclDialog(string shareName, IEnumerable<ShareAce> entries)
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        Title = TitleBar.Title = $"Freigabeberechtigungen – {shareName}";
        Heading.Text = $"Wer darf „{shareName}“ benutzen?";
        _rows = new ObservableCollection<ShareAclRow>(entries.Select(e => new ShareAclRow(e)));
        EntryList.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    public IReadOnlyList<ShareAce> Entries => _rows.Select(r => r.ToAce()).ToList();

    private void UpdateEmpty()
    {
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(() => Helpers.DialogSizing.Fit(this)));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = Ui.Prepare(new PrincipalPickerDialog("Für die Freigabe hinzufügen", includeSpecial: true));
        if (picker.ShowDialog() != true || picker.Selected is not { } p) return;
        if (p.Kind == PrincipalKind.Special && p.SpecialTag != Core.Acl.AceTag.Everyone)
        {
            Ui.Info("Nicht möglich", "Besitzer/Besitzergruppe gibt es nur bei Ordnerrechten. Wähle „Jeder“, einen Benutzer oder eine Gruppe.");
            return;
        }

        var ace = p.Kind == PrincipalKind.Special
            ? new ShareAce(EveryoneSid, "Jeder", null, null, SharePermission.Full, false)
            : new ShareAce(p.Sid, p.Name, p.Kind == PrincipalKind.User ? "USER" : "GROUP", p.Id, SharePermission.Change, false);

        if (_rows.Any(r => r.Who.Equals(ace.Who, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Info("Schon vorhanden", $"{ace.Who} ist bereits eingetragen.");
            return;
        }
        _rows.Add(new ShareAclRow(ace));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShareAclRow row) _rows.Remove(row);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0 &&
            !Ui.Confirm("Niemand hätte Zugriff", "Ohne Einträge kann niemand die Freigabe benutzen. Trotzdem speichern?", "Speichern", true))
            return;
        DialogResult = true;
    }
}

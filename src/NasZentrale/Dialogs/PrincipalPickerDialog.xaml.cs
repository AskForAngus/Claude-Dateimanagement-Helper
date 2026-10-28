using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NasZentrale.Core.Accounts;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public sealed record PrincipalRow(Principal Principal)
{
    public SymbolRegular Icon => Principal.Kind switch
    {
        PrincipalKind.User => SymbolRegular.Person24,
        PrincipalKind.Group => SymbolRegular.People24,
        _ => Principal.SpecialTag == Core.Acl.AceTag.Everyone ? SymbolRegular.Globe24 : SymbolRegular.PersonStar24,
    };
}

/// <summary>Benutzer oder Gruppe aus dem AD (bzw. lokal) auswählen – mit Suche.</summary>
public partial class PrincipalPickerDialog : FluentWindow
{
    private readonly bool _includeSpecial;
    private readonly PrincipalKind? _fixedKind;

    public PrincipalPickerDialog(string title, bool includeSpecial, PrincipalKind? kind = null)
    {
        InitializeComponent();
        _includeSpecial = includeSpecial;
        _fixedKind = kind;
        Title = title;
        TitleBar.Title = title;
        Heading.Text = title;
        if (kind != null) KindFilter.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { Filter(); SearchBox.Focus(); };
    }

    public Principal? Selected { get; private set; }

    private IEnumerable<Principal> All()
    {
        var list = Session.Current.Principals.AsEnumerable();
        if (_includeSpecial) list = new[] { Principal.Everyone, Principal.Owner, Principal.OwnerGroup }.Concat(list);
        return list;
    }

    private void Filter()
    {
        if (!IsLoaded) return;
        var text = SearchBox.Text.Trim();
        var kind = _fixedKind ?? (KindUsers.IsChecked == true ? PrincipalKind.User : KindGroups.IsChecked == true ? PrincipalKind.Group : (PrincipalKind?)null);
        var rows = All()
            .Where(p => kind == null || p.Kind == kind || (p.Kind == PrincipalKind.Special && kind == null))
            .Where(p => text.Length == 0 ||
                        p.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                        (p.FullName?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(p => new PrincipalRow(p))
            .ToList();
        List.ItemsSource = rows;
        if (rows.Count > 0) List.SelectedIndex = 0;
        NoResult.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResolveButton.Content = $"„{text}“ direkt im AD suchen";
        ResolveButton.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Filter();
    private void Kind_Checked(object sender, RoutedEventArgs e) => Filter();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Min(List.SelectedIndex + 1, List.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Max(List.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && List.SelectedItem != null)
        {
            Ok_Click(sender, e);
            e.Handled = true;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => OkButton.IsEnabled = List.SelectedItem != null;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not PrincipalRow row) return;
        Selected = row.Principal;
        DialogResult = true;
    }

    private async void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var name = SearchBox.Text.Trim();
        ResolveButton.IsEnabled = false;
        try
        {
            var p = await Session.Current.Nas.ResolveAsync(name);
            if (p == null || (_fixedKind != null && p.Kind != _fixedKind))
            {
                Ui.Info("Nicht gefunden", $"„{name}“ wurde weder als Benutzer noch als Gruppe gefunden. Versuche es mit DOMÄNE\\name.");
                return;
            }
            Selected = p;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Ui.ShowError("Suche fehlgeschlagen", ex);
        }
        finally
        {
            ResolveButton.IsEnabled = true;
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Session.Current.RefreshPrincipalsAsync();
            Filter();
        }
        catch (Exception ex)
        {
            Ui.ShowError("Liste konnte nicht geladen werden", ex);
        }
    }
}

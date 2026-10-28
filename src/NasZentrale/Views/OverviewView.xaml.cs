using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Accounts;
using NasZentrale.Core.Paths;
using NasZentrale.Dialogs;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Views;

public sealed record OverviewRow(string Path, string Dataset, string Folder, SymbolRegular Icon, string LevelText, int LevelSort, Brush ChipBrush, string Detail)
{
    public string SortKey => Path;
}

/// <summary>„Wer darf wo?“ – Übersicht über alle Stellen mit Rechten, je Benutzer oder Gruppe.</summary>
public partial class OverviewView : UserControl, IPage
{
    private CancellationTokenSource? _scanCts;
    private Principal? _selected;
    private IReadOnlyList<long> _selectedGroups = Array.Empty<long>();

    public OverviewView()
    {
        InitializeComponent();
        FillPrincipals();
        UpdateState();
    }

    private Session S => Session.Current;

    public void OnShown()
    {
        FillPrincipals();
        UpdateState();
    }

    public void MarkStale()
    {
        if (S.Index != null) StaleBar.IsOpen = true;
    }

    private void FillPrincipals()
    {
        var text = SearchBox.Text.Trim();
        var keep = (PrincipalList.SelectedItem as PrincipalRow)?.Principal;
        var rows = S.Principals
            .Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                        (p.FullName?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(p => new PrincipalRow(p))
            .ToList();
        PrincipalList.ItemsSource = rows;
        if (keep != null) PrincipalList.SelectedItem = rows.FirstOrDefault(r => r.Principal == keep);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => FillPrincipals();

    private async void PrincipalList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrincipalList.SelectedItem is not PrincipalRow row || row.Principal == _selected) return;
        _selected = row.Principal;
        _selectedGroups = Array.Empty<long>();
        if (_selected.Kind == PrincipalKind.User)
        {
            try { _selectedGroups = await S.Nas.GetUserGroupIdsAsync(_selected.Name); }
            catch (Exception ex) { Ui.ShowError("Gruppen des Benutzers konnten nicht gelesen werden", ex); }
        }
        UpdateState();
    }

    private void AllFolders_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        PrincipalList.SelectedItem = null;
        UpdateState();
    }

    private void OnlyAccess_Click(object sender, RoutedEventArgs e) => UpdateState();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts != null) return;
        _scanCts = new CancellationTokenSource();
        NoIndexPanel.Visibility = Visibility.Collapsed;
        ResultGrid.Visibility = Visibility.Collapsed;
        NoRowsText.Visibility = Visibility.Collapsed;
        ScanningPanel.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = false;
        ScanText.Text = "Starte …";
        try
        {
            var progress = new Progress<string>(s => ScanText.Text = s);
            S.Index = await AccessIndex.BuildAsync(S.Nas, S.Datasets.Where(d => d.Name.Contains('/')), progress, _scanCts.Token);
            StaleBar.IsOpen = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Ui.ShowError("Rechte konnten nicht eingelesen werden", ex); }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            ScanningPanel.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
            UpdateState();
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void UpdateState()
    {
        if (_scanCts != null) return;
        var index = S.Index;
        ScanButton.Content = index == null ? "Rechte einlesen" : "Neu einlesen";
        IndexInfo.Text = index == null ? "" : $"Stand {index.Created:HH:mm} · {index.TotalDirectories:N0} Ordner geprüft";
        NoIndexPanel.Visibility = index == null ? Visibility.Visible : Visibility.Collapsed;
        ResultGrid.Visibility = index == null ? Visibility.Collapsed : Visibility.Visible;
        OnlyAccessBox.Visibility = _selected != null ? Visibility.Visible : Visibility.Collapsed;

        if (_selected == null)
        {
            ResultTitle.Text = "Ordner mit eigenen Rechten";
            ResultHint.Text = "Alle Stellen, an denen Rechte gesetzt sind. Darunter liegende Ordner erben diese Rechte. Doppelklick öffnet den Ordner.";
            AccessColumn.Header = "Vererbung";
            DetailColumn.Header = "Eigene Einträge";
        }
        else
        {
            ResultTitle.Text = _selected.Display;
            ResultHint.Text = _selected.Kind == PrincipalKind.User
                ? "Zugriff inklusive aller Gruppenmitgliedschaften. Unterordner ohne eigene Rechte haben denselben Zugriff wie ihr übergeordneter Ordner."
                : "Zugriff, den Mitglieder dieser Gruppe über die Gruppe erhalten.";
            AccessColumn.Header = "Zugriff";
            DetailColumn.Header = "Über";
        }

        if (index == null)
        {
            ResultGrid.ItemsSource = null;
            return;
        }

        var rows = _selected == null ? FolderRows(index) : AccessRows(index, _selected);
        ResultGrid.ItemsSource = rows;
        NoRowsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRowsText.Text = _selected == null ? "Keine Ordner gefunden." : $"{_selected.Name} hat nirgends Zugriff.";
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;

    private List<OverviewRow> FolderRows(AccessIndex index) => index.Folders.Select(f =>
    {
        var entries = f.OwnAces
            .GroupBy(a => a.DisplayName)
            .Select(g => $"{g.Key}: {AclRules.Summary(g.First().Perms)}{(g.First().Type == AceType.Deny ? " (verweigert)" : "")}");
        var (text, sort, brush) = f.IsDatasetRoot
            ? ("Dataset", 0, "SystemFillColorAttentionBackgroundBrush")
            : f.Acl.Protected ? ("Aus", 2, "SystemFillColorCautionBackgroundBrush") : ("Erbt + eigene", 1, "SystemFillColorNeutralBackgroundBrush");
        return Row(f, text, sort, Res(brush), string.Join(" · ", entries));
    }).ToList();

    private List<OverviewRow> AccessRows(AccessIndex index, Principal p)
    {
        var onlyAccess = OnlyAccessBox.IsChecked == true;
        return index.Evaluate(p, _selectedGroups)
            .Where(r => !onlyAccess || r.Level != AccessLevel.None)
            .Select(r =>
            {
                var brush = r.Level switch
                {
                    AccessLevel.Full => "SystemFillColorSuccessBackgroundBrush",
                    AccessLevel.Modify => "SystemFillColorAttentionBackgroundBrush",
                    AccessLevel.None => "SystemFillColorCriticalBackgroundBrush",
                    _ => "SystemFillColorNeutralBackgroundBrush",
                };
                var via = r.Via.Count == 0 ? "" : string.Join(", ", r.Via);
                return Row(r.Folder, r.Level.Label(), 5 - (int)r.Level, Res(brush), via);
            })
            .ToList();
    }

    private static OverviewRow Row(IndexedFolder f, string level, int sort, Brush brush, string detail)
    {
        // Dataset-Wurzel: Name normal; Unterordner: Dataset grau davor.
        var dataset = f.IsDatasetRoot ? "" : NasPath.Name(f.DatasetMount);
        var folder = f.IsDatasetRoot ? NasPath.Name(f.DatasetMount) : "› " + NasPath.Relative(f.Path, f.DatasetMount).Replace("/", " › ");
        return new OverviewRow(f.Path, dataset, folder, f.IsDatasetRoot ? SymbolRegular.Database24 : SymbolRegular.Folder24, level, sort, brush, detail);
    }

    private void ResultGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultGrid.SelectedItem is OverviewRow row) Ui.Main?.OpenFolder(row.Path);
    }
}

using System.Windows;
using System.Windows.Media;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Paths;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public sealed record ChangeRow(SymbolRegular Icon, Brush Brush, string Text);

/// <summary>Zeigt vor dem Speichern genau, was sich ändert und wie viele Ordner/Dateien betroffen sind.</summary>
public partial class AclPreviewDialog : FluentWindow
{
    private readonly AclChangePlan _plan;
    private readonly AclChangePlanner _planner;
    private readonly CancellationTokenSource _cts = new();

    public AclPreviewDialog(string name, string displayPath, AclChangePlan plan, AclChangePlanner planner)
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        _plan = plan;
        _planner = planner;
        Heading.Text = $"Rechte von „{name}“";
        PathText.Text = displayPath;

        var rows = new List<ChangeRow>();
        if (plan.ProtectionChanged)
            rows.Add(new ChangeRow(SymbolRegular.ShieldKeyhole24, Brush("SystemFillColorCautionBrush"),
                plan.After.Protected ? "Vererbung von oben wird deaktiviert" : "Vererbung von oben wird wieder aktiviert"));
        foreach (var c in plan.Changes)
        {
            var (icon, brush) = c.Kind switch
            {
                AceChangeKind.Added => (SymbolRegular.AddCircle24, "SystemFillColorSuccessBrush"),
                AceChangeKind.Removed => (SymbolRegular.DismissCircle24, "SystemFillColorCriticalBrush"),
                _ => (SymbolRegular.Edit24, "AccentTextFillColorPrimaryBrush"),
            };
            rows.Add(new ChangeRow(icon, Brush(brush), c.Text));
        }
        ChangeList.ItemsSource = rows;

        Loaded += async (_, _) => await AnalyzeAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private async Task AnalyzeAsync()
    {
        if (!_plan.IsDirectory)
        {
            EffectText.Text = "Nur diese Datei wird geändert.";
            ApplyButton.IsEnabled = true;
            return;
        }
        if (!_plan.NeedsPropagation)
        {
            EffectText.Text = "Nur dieser Ordner wird geändert. Unterordner und Dateien sind nicht betroffen, weil sich an der Vererbung nichts ändert.";
            ApplyButton.IsEnabled = true;
            return;
        }

        ScanBusy.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<string>(s => ScanText.Text = s);
            _plan.Scan = await _planner.ScanAsync(_plan.Before, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ScanBusy.Visibility = Visibility.Collapsed;
            EffectText.Text = "Die Unterordner konnten nicht geprüft werden: " + Ui.Describe(ex);
            return;
        }

        var scan = _plan.Scan;
        ScanBusy.Visibility = Visibility.Collapsed;
        EffectText.Text = scan.Directories + scan.Files == 0
            ? "Dieser Ordner ist leer – nur er selbst wird geändert."
            : $"Die Änderung wird an {Count(scan.Directories, "Unterordner", "Unterordner")} und {Count(scan.Files, "Datei", "Dateien")} darunter weitergegeben.";

        if (scan.CustomFolders.Count > 0)
        {
            CustomPanel.Visibility = Visibility.Visible;
            CustomTitle.Text = scan.CustomFolders.Count == 1
                ? "1 Ordner mit eigenen Rechten behält diese:"
                : $"{scan.CustomFolders.Count} Ordner mit eigenen Rechten behalten diese:";
            var root = _plan.Before.Path;
            var names = scan.CustomFolders.Take(12)
                .Select(c => NasPath.Relative(c.Path, root) + (c.Protected ? "  (Vererbung aus – bleibt unverändert)" : ""))
                .ToList();
            if (scan.CustomFolders.Count > 12) names.Add($"… und {scan.CustomFolders.Count - 12} weitere");
            CustomList.ItemsSource = names;
        }

        var notes = new List<string>();
        if (scan.ChildDatasets.Count > 0)
            notes.Add($"{scan.ChildDatasets.Count} untergeordnete(s) Dataset(s) bleiben unverändert – Rechte dort bitte separat setzen.");
        if (scan.Files > 0)
            notes.Add("Einzelne Dateien mit individuell gesetzten Rechten erhalten wieder die vererbten Rechte.");
        if (notes.Count > 0)
        {
            NoteText.Text = string.Join("\n", notes);
            NoteText.Visibility = Visibility.Visible;
        }
        ApplyButton.IsEnabled = true;
        Helpers.DialogSizing.Fit(this);
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        DialogResult = false;
    }
}

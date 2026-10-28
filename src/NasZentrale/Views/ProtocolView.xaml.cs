using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Paths;
using NasZentrale.Core.Protocol;
using NasZentrale.Core.TrueNas;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Views;

public sealed record ProtocolRow(ProtocolEntry Entry, ProtocolEntry? UndoneBy)
{
    public string Summary => Entry.Summary;
    public string Target => Entry.Target;
    public string Meta => $"{Entry.Time.LocalDateTime:dd.MM.yyyy HH:mm} · {Entry.User} · {Entry.Category}" +
                          (UndoneBy != null ? $" · rückgängig gemacht am {UndoneBy.Time.LocalDateTime:dd.MM.yyyy HH:mm} von {UndoneBy.User}" : "");
    public bool HasDetails => Entry.Details.Count > 0;
    public string DetailsText => string.Join("\n", Entry.Details.Select(d => "• " + d));
    public bool IsUndone => UndoneBy != null;
    public bool CanUndo => Entry.Undo != null && UndoneBy == null && Entry.UndoOf == null;

    public SymbolRegular Icon => Entry.UndoOf != null ? SymbolRegular.ArrowUndo24 : Entry.Category switch
    {
        ProtocolCategory.Rights => SymbolRegular.ShieldKeyhole24,
        ProtocolCategory.Files => SymbolRegular.Document24,
        ProtocolCategory.Shares => SymbolRegular.Server24,
        ProtocolCategory.Datasets => SymbolRegular.Database24,
        _ => SymbolRegular.Info24,
    };
}

/// <summary>Gemeinsames Protokoll aller Admins mit „Rückgängig“ für Rechteänderungen.</summary>
public partial class ProtocolView : UserControl, IPage
{
    private List<ProtocolEntry> _all = new();
    private int _limit = 200;
    private bool _dirty = true;

    public ProtocolView()
    {
        InitializeComponent();
        Session.Current.ProtocolChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            _dirty = true;
            if (IsVisible) _ = LoadAsync();
        }));
    }

    private Session S => Session.Current;

    public void OnShown()
    {
        if (_dirty) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _dirty = false;
        Loading.Visibility = Visibility.Visible;
        try { _all = await S.Protocol.LoadAsync(); }
        catch (Exception ex) { Ui.ShowError("Protokoll konnte nicht gelesen werden", ex); }
        finally { Loading.Visibility = Visibility.Collapsed; }

        SubTitle.Text = S.ProtocolIsShared
            ? $"Gemeinsam für alle Admins · gespeichert auf dem NAS ({S.Settings.ProtocolFolder})"
            : "Wer hat wann was geändert.";
        LocalPanel.Visibility = S.ProtocolIsShared ? Visibility.Collapsed : Visibility.Visible;
        LocalBar.Message = (S.ProtocolProblem ?? "Es ist noch kein gemeinsamer Protokollordner auf dem NAS eingerichtet.") +
                           " Einträge werden nur auf diesem PC gespeichert und sind für andere Admins nicht sichtbar.";
        Apply();
    }

    private void Apply()
    {
        if (!IsLoaded && EntryList == null) return;
        var text = SearchBox.Text.Trim();
        var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Alle";
        var undoneBy = _all.Where(e => e.UndoOf != null).GroupBy(e => e.UndoOf!.Value).ToDictionary(g => g.Key, g => g.First());

        var filtered = _all
            .Where(e => category == "Alle" || e.Category == category)
            .Where(e => text.Length == 0 ||
                        e.Summary.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                        e.Target.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                        e.User.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                        e.Details.Any(d => d.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        EntryList.ItemsSource = filtered.Take(_limit).Select(e => new ProtocolRow(e, undoneBy.GetValueOrDefault(e.Id))).ToList();
        MoreButton.Visibility = filtered.Count > _limit ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0 ? "Noch keine Einträge." : "Keine passenden Einträge.";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _limit = 200;
        Apply();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _limit += 200;
        Apply();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        Ui.Prepare(new Dialogs.SetupDialog()).ShowDialog();
        await LoadAsync();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProtocolRow row || row.Entry.Undo is not { } undo) return;
        var entry = row.Entry;

        // Spätere Änderungen an denselben Ordnern würden mit überschrieben – darauf hinweisen.
        var later = _all
            .Where(x => x.Time > entry.Time && x.Category == entry.Category && x.UndoOf == null &&
                        (NasPath.IsUnder(x.Target, entry.Target) || NasPath.IsUnder(entry.Target, x.Target)))
            .ToList();
        var message = $"„{entry.Summary}“ vom {entry.Time.LocalDateTime:dd.MM.yyyy HH:mm} ({entry.User}) wird rückgängig gemacht. " +
                      "Der Zustand von vorher wird wiederhergestellt.";
        string? details = null;
        if (later.Count > 0)
        {
            message += $"\n\nAchtung: Danach gab es {later.Count} weitere Änderung(en) in diesem Bereich. Diese können dabei überschrieben werden.";
            details = string.Join("\n", later.Select(l => $"{l.Time.LocalDateTime:dd.MM. HH:mm} {l.User}: {l.Summary}"));
        }
        if (!Ui.Confirm("Rückgängig machen", message, "Rückgängig machen", later.Count > 0, details)) return;

        var kind = undo.Str("kind");
        var ok = await Ui.RunAsync("Wird rückgängig gemacht …", async scope =>
        {
            if (kind == "acl")
                await new AclChangePlanner(S.Nas).UndoAsync(undo, scope.Progress, CancellationToken.None);
            else if (kind == "shareacl")
                await S.Nas.SetShareAclAsync(undo.Str("share")!, ParseShareAcl(undo));
            else
                throw new InvalidOperationException("Dieser Eintrag kann nicht rückgängig gemacht werden.");
        }, errorTitle: "Rückgängig machen fehlgeschlagen");
        if (!ok) return;

        await S.LogAsync(new ProtocolEntry
        {
            Category = entry.Category,
            Action = "Rückgängig gemacht",
            Target = entry.Target,
            Summary = $"Rückgängig: {entry.Summary}",
            Details = { $"Ursprüngliche Änderung vom {entry.Time.LocalDateTime:dd.MM.yyyy HH:mm} durch {entry.User}" },
            UndoOf = entry.Id,
        });
        Ui.Main?.InvalidateOverview();
        Ui.Toast("Änderung rückgängig gemacht.");
    }

    private static IEnumerable<ShareAce> ParseShareAcl(JsonObject undo) =>
        undo["entries"].Objects().Select(o => new ShareAce(
            o.Str("sid"),
            o.Str("who") ?? "",
            o.Str("idType"),
            o.Long("id"),
            Enum.TryParse<SharePermission>(o.Str("perm"), out var p) ? p : SharePermission.Read,
            o.Bool("deny")));
}

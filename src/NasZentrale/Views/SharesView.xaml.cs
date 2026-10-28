using System.Windows;
using System.Windows.Controls;
using System.Text.Json.Nodes;
using NasZentrale.Core.Protocol;
using NasZentrale.Core.TrueNas;
using NasZentrale.Dialogs;
using NasZentrale.Models;
using NasZentrale.Services;

namespace NasZentrale.Views;

public sealed record ShareRow(SmbShare Share, string Unc)
{
    public string Name => Share.Name;
    public string Path => Share.Path;
    public bool Enabled => Share.Enabled;
    public bool ReadOnly => Share.ReadOnly == true;
    public string CommentSuffix => string.IsNullOrWhiteSpace(Share.Comment) ? "" : "  ·  " + Share.Comment;
}

public sealed record DatasetRow(DatasetInfo Dataset, bool Shared)
{
    public string Name => Dataset.Name;
    public string Mountpoint => Dataset.Mountpoint;
    public bool WindowsReady => Dataset.IsNfs4;
    public double UsedPercent => Dataset.Used + Dataset.Available == 0 ? 0 : 100.0 * Dataset.Used / (Dataset.Used + Dataset.Available);
    public string UsageText => $"{FileItem.FormatSize(Dataset.Used)} belegt · {FileItem.FormatSize(Dataset.Available)} frei";
}

/// <summary>SMB-Freigaben und Datasets verwalten.</summary>
public partial class SharesView : UserControl, IPage
{
    public SharesView()
    {
        InitializeComponent();
        Fill();
        Session.Current.SharesChanged += () => Dispatcher.Invoke(Fill);
        Session.Current.DatasetsChanged += () => Dispatcher.Invoke(Fill);
    }

    private Session S => Session.Current;

    public void OnShown() => Fill();

    private void Fill()
    {
        ShareList.ItemsSource = S.Shares.Select(s => new ShareRow(s, $@"\\{S.SmbHost}\{s.Name}")).ToList();
        NoShares.Visibility = S.Shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DatasetList.ItemsSource = S.Datasets.Where(d => d.Name.Contains('/'))
            .Select(d => new DatasetRow(d, S.Mapper.ShareFor(d.Mountpoint) != null)).ToList();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await Ui.RunAsync("Lade Freigaben …", async _ =>
        {
            await S.RefreshDatasetsAsync();
            await S.RefreshSharesAsync();
        });
    }

    private static T? RowOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<ShareRow>(sender) is { } row) Ui.Main?.OpenFolder(row.Path);
    }

    private void OpenDataset_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<DatasetRow>(sender) is { } row) Ui.Main?.OpenFolder(row.Mountpoint);
    }

    private async void NewShare_Click(object sender, RoutedEventArgs e)
    {
        var dlg = Ui.Prepare(new ShareEditDialog(null));
        if (dlg.ShowDialog() != true) return;
        SmbShare? created = null;
        var ok = await Ui.RunAsync("Freigabe wird angelegt …", async _ =>
        {
            created = await S.Nas.CreateShareAsync(dlg.ShareName, dlg.SharePath, dlg.Comment, dlg.ShareEnabled, dlg.Abe, dlg.ReadOnly);
            await S.RefreshSharesAsync();
        }, errorTitle: "Freigabe konnte nicht angelegt werden");
        if (!ok) return;
        await S.LogAsync(new ProtocolEntry
        {
            Category = ProtocolCategory.Shares,
            Action = "Freigabe angelegt",
            Target = dlg.SharePath,
            Summary = $"Freigabe „{dlg.ShareName}“ angelegt",
            Details = { $@"\\{S.SmbHost}\{dlg.ShareName} → {dlg.SharePath}" },
        });
        Ui.Toast($"Freigabe „{dlg.ShareName}“ angelegt.");
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<ShareRow>(sender) is not { } row) return;
        var dlg = Ui.Prepare(new ShareEditDialog(row.Share));
        if (dlg.ShowDialog() != true) return;
        var ok = await Ui.RunAsync("Freigabe wird gespeichert …", async _ =>
        {
            await S.Nas.UpdateShareAsync(row.Share.Id, dlg.ShareName, dlg.SharePath, dlg.Comment, dlg.ShareEnabled, dlg.Abe, dlg.ReadOnly);
            await S.RefreshSharesAsync();
        }, errorTitle: "Freigabe konnte nicht gespeichert werden");
        if (!ok) return;

        var details = new List<string>();
        if (row.Share.Name != dlg.ShareName) details.Add($"Name: {row.Share.Name} → {dlg.ShareName}");
        if (row.Share.Path != dlg.SharePath) details.Add($"Ordner: {row.Share.Path} → {dlg.SharePath}");
        if (row.Share.Comment != dlg.Comment) details.Add($"Beschreibung: {dlg.Comment}");
        if (row.Share.Enabled != dlg.ShareEnabled) details.Add(dlg.ShareEnabled ? "Aktiviert" : "Deaktiviert");
        if (row.Share.AccessBasedEnumeration != dlg.Abe) details.Add($"Nur sichtbare Ordner zeigen: {(dlg.Abe == true ? "an" : "aus")}");
        if (row.Share.ReadOnly != dlg.ReadOnly) details.Add($"Schreibgeschützt: {(dlg.ReadOnly == true ? "an" : "aus")}");
        await S.LogAsync(new ProtocolEntry
        {
            Category = ProtocolCategory.Shares,
            Action = "Freigabe geändert",
            Target = dlg.SharePath,
            Summary = $"Freigabe „{dlg.ShareName}“ geändert",
            Details = details,
        });
        Ui.Toast("Freigabe gespeichert.");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<ShareRow>(sender) is not { } row) return;
        if (!Ui.Confirm("Freigabe entfernen",
                $"Die Freigabe „{row.Name}“ wird entfernt. Die Dateien im Ordner {row.Path} bleiben erhalten, sind aber nicht mehr über {row.Unc} erreichbar.",
                "Freigabe entfernen", true))
            return;
        var ok = await Ui.RunAsync("Freigabe wird entfernt …", async _ =>
        {
            await S.Nas.DeleteShareAsync(row.Share.Id);
            await S.RefreshSharesAsync();
        }, errorTitle: "Freigabe konnte nicht entfernt werden");
        if (!ok) return;
        await S.LogAsync(new ProtocolEntry
        {
            Category = ProtocolCategory.Shares,
            Action = "Freigabe entfernt",
            Target = row.Path,
            Summary = $"Freigabe „{row.Name}“ entfernt",
        });
        Ui.Toast($"Freigabe „{row.Name}“ entfernt.");
    }

    private async void ShareAcl_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<ShareRow>(sender) is not { } row) return;
        IReadOnlyList<ShareAce>? acl = null;
        if (!await Ui.RunAsync("Lade Freigabeberechtigungen …", async _ => acl = await S.Nas.GetShareAclAsync(row.Name))) return;

        var dlg = Ui.Prepare(new ShareAclDialog(row.Name, acl!));
        if (dlg.ShowDialog() != true) return;
        var before = acl!;
        var after = dlg.Entries;
        var ok = await Ui.RunAsync("Freigabeberechtigungen werden gespeichert …",
            _ => S.Nas.SetShareAclAsync(row.Name, after), errorTitle: "Freigabeberechtigungen konnten nicht gespeichert werden");
        if (!ok) return;

        await S.LogAsync(new ProtocolEntry
        {
            Category = ProtocolCategory.Shares,
            Action = "Freigabeberechtigungen geändert",
            Target = row.Path,
            Summary = $"Freigabeberechtigungen von „{row.Name}“ geändert",
            Details = after.Select(a => $"{a.Who}: {a.PermissionLabel}{(a.Deny ? " (verweigert)" : "")}").ToList(),
            Undo = ShareAclUndo(row.Name, before),
        });
        Ui.Toast("Freigabeberechtigungen gespeichert.");
    }

    public static JsonObject ShareAclUndo(string shareName, IEnumerable<ShareAce> entries) => new()
    {
        ["kind"] = "shareacl",
        ["share"] = shareName,
        ["entries"] = new JsonArray(entries.Select(a => (JsonNode)new JsonObject
        {
            ["sid"] = a.Sid,
            ["who"] = a.Who,
            ["idType"] = a.IdType,
            ["id"] = a.Id,
            ["perm"] = a.Permission.ToString(),
            ["deny"] = a.Deny,
        }).ToArray()),
    };

    private async void NewDataset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = Ui.Prepare(new DatasetCreateDialog());
        if (dlg.ShowDialog() != true) return;
        DatasetInfo? created = null;
        var ok = await Ui.RunAsync("Dataset wird angelegt …", async _ =>
        {
            created = await S.Nas.CreateDatasetAsync(dlg.FullName);
            await S.RefreshDatasetsAsync();
            if (dlg.CreateShare && created != null)
            {
                await S.Nas.CreateShareAsync(dlg.ShareName, created.Mountpoint, "", true, null, null);
                await S.RefreshSharesAsync();
            }
        }, errorTitle: "Dataset konnte nicht angelegt werden");
        if (!ok) return;

        await S.LogAsync(new ProtocolEntry
        {
            Category = ProtocolCategory.Datasets,
            Action = "Dataset angelegt",
            Target = created?.Mountpoint ?? dlg.FullName,
            Summary = $"Dataset „{dlg.FullName}“ angelegt" + (dlg.CreateShare ? $" und als „{dlg.ShareName}“ freigegeben" : ""),
        });
        if (created != null &&
            Ui.Confirm("Dataset angelegt", "Möchtest du jetzt die Rechte für das neue Dataset festlegen?", "Rechte festlegen"))
            Ui.Main?.OpenFolder(created.Mountpoint);
    }
}

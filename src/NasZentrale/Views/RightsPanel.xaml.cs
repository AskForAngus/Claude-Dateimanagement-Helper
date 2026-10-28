using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Accounts;
using NasZentrale.Core.Paths;
using NasZentrale.Core.Protocol;
using NasZentrale.Dialogs;
using NasZentrale.Models;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Views;

/// <summary>Rechte eines Ordners/einer Datei: ansehen, bearbeiten, mit Vorschau speichern.</summary>
public partial class RightsPanel : UserControl
{
    private readonly ObservableCollection<AceItem> _items = new();
    private NfsAcl? _original;
    private NfsAcl? _working;
    private string _path = "";
    private string _name = "";
    private bool _isDirectory;
    private bool _dirty;
    private int _loadVersion;

    public RightsPanel()
    {
        InitializeComponent();
        AceList.ItemsSource = _items;
        Clear();
    }

    /// <summary>Wunsch, im Dateibereich zu einem Ordner zu springen (z. B. Ursprung eines geerbten Rechts).</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>Nach dem Speichern (z. B. um Markierungen im Baum zu aktualisieren).</summary>
    public event Action<string>? Saved;

    public string CurrentPath => _path;
    public bool HasUnsavedChanges => _dirty;

    private Session S => Session.Current;

    /// <summary>Fragt bei ungespeicherten Änderungen nach. True = weitermachen.</summary>
    public bool ConfirmLeave()
    {
        if (!_dirty) return true;
        if (!Ui.Confirm("Änderungen verwerfen?", $"Die Rechte von „{_name}“ wurden geändert, aber nicht gespeichert. Änderungen verwerfen?", "Verwerfen", true))
            return false;
        SetDirty(false);
        return true;
    }

    public void Clear()
    {
        _loadVersion++;
        _original = _working = null;
        _path = "";
        _items.Clear();
        Scroller.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        LoadingRing.Visibility = Visibility.Collapsed;
        ObjectName.Text = "";
        PathText.Text = "";
        Chips.Visibility = Visibility.Collapsed;
        SetDirty(false);
    }

    public async Task LoadAsync(string path, bool isDirectory, string displayName)
    {
        var version = ++_loadVersion;
        _path = path;
        _name = displayName;
        _isDirectory = isDirectory;
        ObjectName.Text = displayName;
        ObjectIcon.Symbol = isDirectory ? SymbolRegular.Folder24 : SymbolRegular.Document24;
        PathText.Text = DisplayPath(path);
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingRing.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
        SetDirty(false);

        try
        {
            var acl = await S.Nas.GetAclAsync(path);
            if (version != _loadVersion) return;
            _original = acl;
            _working = acl.Clone();
            Rebuild();
            Scroller.Visibility = Visibility.Visible;
            Scroller.ScrollToTop();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            Clear();
            Ui.ShowError("Rechte konnten nicht gelesen werden", ex);
        }
        finally
        {
            if (version == _loadVersion) LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    public Task ReloadAsync() => _path.Length == 0 ? Task.CompletedTask : LoadAsync(_path, _isDirectory, _name);

    private string DisplayPath(string path)
    {
        var unc = S.Mapper.ToUnc(path);
        return unc ?? path;
    }

    private bool IsDatasetRoot => S.Datasets.Any(d => d.Mountpoint == _path);

    private void Rebuild(NfsAce? select = null)
    {
        var acl = _working!;
        var previous = select ?? (AceList.SelectedItem as AceItem)?.Ace;
        _items.Clear();

        var ordered = acl.Aces
            .OrderBy(a => a.IsInherited)
            .ThenBy(a => a.Type == AceType.Allow)
            .ToList();
        foreach (var ace in ordered) _items.Add(new AceItem(ace, _isDirectory, () => SetDirty(true)));

        NotNfsBar.IsOpen = !acl.IsNfs4;
        OwnerPanel.IsEnabled = AddButton.IsEnabled = acl.IsNfs4;
        NoEntriesText.Visibility = acl.IsNfs4 && _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        OwnerText.Text = $"{acl.OwnerName ?? acl.Uid.ToString()}  ·  Gruppe: {acl.OwnerGroupName ?? acl.Gid.ToString()}";

        var datasetRoot = IsDatasetRoot;
        InheritSwitch.IsChecked = !acl.Protected && !datasetRoot;
        InheritSwitch.IsEnabled = acl.IsNfs4 && !datasetRoot;
        InheritSwitch.Visibility = datasetRoot ? Visibility.Collapsed : Visibility.Visible;
        InheritHint.Text = datasetRoot
            ? "Oberste Ebene des Datasets – hier beginnt die Vererbung."
            : acl.Protected
                ? "Aus: Änderungen weiter oben wirken hier nicht."
                : "An: Rechte der übergeordneten Ordner gelten auch hier.";

        var own = _original!.ExplicitAces.Any() && !datasetRoot;
        Chips.Visibility = Visibility.Visible;
        ChipOwn.Visibility = acl.IsNfs4 && (own || acl.Protected) ? Visibility.Visible : Visibility.Collapsed;
        ChipInherits.Visibility = acl.IsNfs4 && !own && !acl.Protected && !datasetRoot ? Visibility.Visible : Visibility.Collapsed;
        ChipProtected.Visibility = acl.Protected && !datasetRoot ? Visibility.Visible : Visibility.Collapsed;
        ChipNoShare.Visibility = S.Mapper.ShareFor(_path) == null ? Visibility.Visible : Visibility.Collapsed;

        var again = previous == null ? null : _items.FirstOrDefault(i => ReferenceEquals(i.Ace, previous));
        AceList.SelectedItem = again;
        ShowEditor(again);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        SaveBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Auswahl & Editor ----------

    private void AceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowEditor(AceList.SelectedItem as AceItem);

    private void ShowEditor(AceItem? item)
    {
        Editor.DataContext = item;
        Editor.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
        if (item is { IsInherited: true, InheritedFrom: null }) _ = FindOriginAsync(item);
        // Erst nach dem Klick (und nur wenn nötig) zum Editor scrollen – sonst springt die Liste unter der Maus weg.
        if (item != null) Dispatcher.BeginInvoke(new Action(ScrollEditorIntoView), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void ScrollEditorIntoView()
    {
        if (Editor.Visibility != Visibility.Visible || !Editor.IsLoaded) return;
        try
        {
            var top = Editor.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
            if (top > Scroller.ViewportHeight - 160)
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + top - 80);
        }
        catch (InvalidOperationException) { /* noch nicht im Layout */ }
    }

    /// <summary>Die Liste liegt in einem ScrollViewer – Mausrad an diesen weitergeben.</summary>
    private void AceList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 3.0);
    }

    /// <summary>Sucht den Ordner, von dem ein geerbter Eintrag stammt.</summary>
    private async Task FindOriginAsync(AceItem item)
    {
        var dataset = S.DatasetFor(_path);
        if (dataset == null) return;
        try
        {
            foreach (var folder in NasPath.Chain(dataset.Mountpoint, NasPath.Parent(_path)).Reverse())
            {
                var acl = await S.Nas.GetAclAsync(folder);
                var match = acl.Aces.Any(a => !a.IsInherited && a.SamePrincipal(item.Ace) && a.Type == item.Ace.Type && a.Perms == item.Ace.Perms);
                if (match || acl.Protected || folder == dataset.Mountpoint)
                {
                    item.InheritedFromPath = folder;
                    item.InheritedFrom = folder == dataset.Mountpoint
                        ? $"{dataset.ShortName} (Dataset)"
                        : NasPath.Relative(folder, dataset.Mountpoint);
                    return;
                }
            }
        }
        catch (Exception) { /* nur Zusatzinfo */ }
    }

    private void GoToOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.DataContext is AceItem { InheritedFromPath: { } path }) NavigateRequested?.Invoke(path);
    }

    // ---------- Bearbeiten ----------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        var picker = Ui.Prepare(new PrincipalPickerDialog("Benutzer oder Gruppe hinzufügen", includeSpecial: true));
        if (picker.ShowDialog() != true || picker.Selected is not { } principal) return;

        var existing = _items.FirstOrDefault(i => !i.IsInherited && i.IsAllow && i.Ace.SamePrincipal(principal.ToAce(NfsPerm.None, NfsFlag.None)));
        if (existing != null)
        {
            AceList.SelectedItem = existing;
            Ui.Toast($"{principal.Name} ist bereits eingetragen – Rechte unten anpassen.", InfoBarSeverity.Informational);
            return;
        }

        var flags = _isDirectory ? NfsFlag.FileInherit | NfsFlag.DirectoryInherit : NfsFlag.None;
        var ace = principal.ToAce(NfsPerm.Read, flags);
        _working.Aces.Insert(0, ace);
        SetDirty(true);
        Rebuild(ace);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.DataContext is not AceItem item || _working == null) return;
        _working.Aces.Remove(item.Ace);
        SetDirty(true);
        AceList.SelectedItem = null;
        Rebuild();
    }

    private async void InheritSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        var turnOn = InheritSwitch.IsChecked == true;

        if (!turnOn)
        {
            var choice = Ui.Ask("Vererbung deaktivieren",
                "Danach wirken Änderungen an übergeordneten Ordnern hier nicht mehr. Was soll mit den bisher geerbten Einträgen passieren?",
                "Als eigene Einträge behalten", "Entfernen");
            if (choice == DialogChoice.Primary)
                foreach (var a in _working.Aces) a.Flags &= ~NfsFlag.Inherited;
            else if (choice == DialogChoice.Tertiary)
                _working.Aces.RemoveAll(a => a.IsInherited);
            else
            {
                InheritSwitch.IsChecked = true;
                return;
            }
            _working.Protected = true;
        }
        else
        {
            try
            {
                var parent = await S.Nas.GetAclAsync(NasPath.Parent(_path));
                _working.Aces.RemoveAll(a => a.IsInherited);
                _working.Aces.AddRange(AclRules.InheritedFor(parent.Aces, _isDirectory));
                _working.Protected = false;
            }
            catch (Exception ex)
            {
                InheritSwitch.IsChecked = false;
                Ui.ShowError("Übergeordnete Rechte konnten nicht gelesen werden", ex);
                return;
            }
        }
        SetDirty(true);
        Rebuild();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        _working = _original.Clone();
        SetDirty(false);
        AceList.SelectedItem = null;
        Rebuild();
    }

    // ---------- Speichern ----------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null || _working == null) return;
        var after = _working.Clone();

        if (after.Aces.Count == 0 &&
            !Ui.Confirm("Niemand hätte Zugriff", "Es gibt keinen einzigen Eintrag mehr. Dann kann niemand (außer über TrueNAS) auf diesen Ordner zugreifen. Trotzdem speichern?", "Trotzdem speichern", true))
            return;
        if (after.ExplicitAces.Any(a => a.Perms == NfsPerm.None))
        {
            Ui.Info("Eintrag ohne Rechte", "Mindestens ein Eintrag hat kein einziges Häkchen. Entferne ihn oder wähle Rechte aus.");
            return;
        }

        var plan = AclChangePlanner.CreatePlan(_original, after, _isDirectory);
        if (plan.Changes.Count == 0 && !plan.ProtectionChanged)
        {
            SetDirty(false);
            Rebuild();
            Ui.Toast("Es gab nichts zu speichern.", InfoBarSeverity.Informational);
            return;
        }

        var planner = new AclChangePlanner(S.Nas);
        var preview = Ui.Prepare(new AclPreviewDialog(_name, DisplayPath(_path), plan, planner));
        if (preview.ShowDialog() != true) return;

        var path = _path;
        System.Text.Json.Nodes.JsonObject? undo = null;
        var ok = await Ui.RunAsync("Rechte werden gespeichert …", async scope =>
        {
            undo = await planner.ApplyAsync(plan, scope.Progress, CancellationToken.None);
        }, errorTitle: "Rechte konnten nicht gespeichert werden");

        if (ok)
        {
            var details = plan.Changes.Select(c => c.Text).ToList();
            if (plan.ProtectionChanged) details.Insert(0, after.Protected ? "Vererbung deaktiviert" : "Vererbung aktiviert");
            if (plan.NeedsPropagation && plan.Scan != null)
                details.Add($"Weitergegeben an {plan.Scan.Directories} Ordner und {plan.Scan.Files} Dateien; " +
                            $"{plan.Scan.CustomFolders.Count} Ordner mit eigenen Rechten beibehalten.");
            await S.LogAsync(new ProtocolEntry
            {
                Category = ProtocolCategory.Rights,
                Action = "Rechte geändert",
                Target = path,
                Summary = $"Rechte von „{_name}“ geändert",
                Details = details,
                Undo = undo,
            });
            Ui.Toast($"Rechte von „{_name}“ gespeichert.");
            Ui.Main?.InvalidateOverview();
            Saved?.Invoke(path);
        }
        await ReloadAsync();
    }

    // ---------- Besitzer ----------

    private async void ChangeOwner_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        var choice = Ui.Ask("Besitzer ändern", "Was möchtest du ändern?", "Besitzer (Benutzer)", "Besitzergruppe");
        if (choice == DialogChoice.None || choice == DialogChoice.Secondary) return;
        var userMode = choice == DialogChoice.Primary;

        var picker = Ui.Prepare(new PrincipalPickerDialog(userMode ? "Neuen Besitzer wählen" : "Neue Besitzergruppe wählen",
            includeSpecial: false, kind: userMode ? PrincipalKind.User : PrincipalKind.Group));
        if (picker.ShowDialog() != true || picker.Selected is not { } p) return;

        var recursive = false;
        if (_isDirectory)
        {
            var scope = Ui.Ask("Für Unterordner übernehmen?",
                $"Soll „{p.Name}“ auch Besitzer aller Unterordner und Dateien werden?", "Nur dieser Ordner", "Auch alle Unterobjekte");
            if (scope == DialogChoice.None || scope == DialogChoice.Secondary) return;
            recursive = scope == DialogChoice.Tertiary;
        }

        var path = _path;
        var before = userMode ? _original.OwnerName : _original.OwnerGroupName;
        var ok = await Ui.RunAsync("Besitzer wird geändert …", async _ =>
        {
            await S.Nas.ChownAsync(path, userMode ? p.Id : null, userMode ? null : p.Id, recursive);
        }, errorTitle: "Besitzer konnte nicht geändert werden");
        if (!ok) return;

        await S.LogAsync(new ProtocolEntry
        {
            Category = ProtocolCategory.Rights,
            Action = userMode ? "Besitzer geändert" : "Besitzergruppe geändert",
            Target = path,
            Summary = $"{(userMode ? "Besitzer" : "Besitzergruppe")} von „{_name}“: {before} → {p.Name}",
            Details = recursive ? new List<string> { "Inklusive aller Unterordner und Dateien" } : new List<string>(),
        });
        Ui.Toast("Besitzer geändert.");
        await ReloadAsync();
    }

    // ---------- Zugriff prüfen ----------

    private async void CheckAccess_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        var picker = Ui.Prepare(new PrincipalPickerDialog("Zugriff prüfen für …", includeSpecial: false));
        if (picker.ShowDialog() != true || picker.Selected is not { } p) return;

        try
        {
            IReadOnlyCollection<long> gids = p.Kind == PrincipalKind.User ? await S.Nas.GetUserGroupIdsAsync(p.Name) : new[] { p.Id };
            var uid = p.Kind == PrincipalKind.User ? p.Id : long.MinValue;
            var perms = AclRules.Evaluate(_original, uid, gids);
            var level = AclRules.AtLeastLevel(perms);
            var lines = AclRules.PermissionLabels.Select(l => $"{(perms.HasFlag(l.Perm) ? "✔" : "✘")}  {l.Label}");
            var text = level == AccessLevel.None
                ? $"{p.Name} hat auf „{_name}“ keinen Zugriff."
                : $"{p.Name} hat auf „{_name}“: {level.Label()}.";
            if (_dirty) text += "\n\nHinweis: Berechnet mit den gespeicherten Rechten, nicht mit deinen ungespeicherten Änderungen.";
            text += "\n\nBerücksichtigt Gruppenmitgliedschaften. Freigabeberechtigungen können den Zugriff über das Netzwerk zusätzlich einschränken.";
            Ui.Info("Effektiver Zugriff", text, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            Ui.ShowError("Zugriff konnte nicht geprüft werden", ex);
        }
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Paths;
using NasZentrale.Core.Protocol;
using NasZentrale.Helpers;
using NasZentrale.Models;
using NasZentrale.Services;
using Wpf.Ui.Controls;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace NasZentrale.Views;

public sealed record Crumb(string Name, string Path, bool ShowSeparator);

/// <summary>Hauptseite: Ordnerbaum, Dateiliste mit Dateiaktionen und Rechte-Bereich.</summary>
public partial class FilesView : UserControl, IPage
{
    private readonly ObservableCollection<FolderNode> _roots = new();
    private readonly ObservableCollection<FileItem> _items = new();
    private readonly ICollectionView _view;
    private readonly Stack<string> _history = new();
    private string? _currentPath;
    private string? _currentUnc;
    private bool _syncingTree;
    private int _listVersion;

    /// <summary>Interne Zwischenablage für Kopieren/Ausschneiden zwischen NAS-Ordnern.</summary>
    private static List<string> _clipboard = new();
    private static bool _clipboardIsCut;

    public FilesView()
    {
        InitializeComponent();
        FolderTree.ItemsSource = _roots;
        FolderTree.ItemContainerStyle = StyleHelper.Derive(this, typeof(TreeViewItem),
            (TreeViewItem.IsExpandedProperty, nameof(FolderNode.IsExpanded)),
            (TreeViewItem.IsSelectedProperty, nameof(FolderNode.IsSelected)));

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is FileItem f && (FilterBox.Text.Length == 0 || f.Name.Contains(FilterBox.Text, StringComparison.CurrentCultureIgnoreCase));
        FileGrid.ItemsSource = _view;

        Rights.NavigateRequested += path => OpenFolder(path);
        Rights.Saved += OnRightsSaved;
        Session.Current.DatasetsChanged += () => Dispatcher.Invoke(BuildRoots);
        Session.Current.SharesChanged += () => Dispatcher.Invoke(BuildRoots);

        BuildRoots();
        Loaded += (_, _) =>
        {
            if (_currentPath != null) return;
            var last = Session.Current.Settings.LastFolder;
            if (last.Length > 0 && Session.Current.DatasetFor(last) != null) OpenFolder(last);
            else if (_roots.Count > 0) OpenFolder(_roots[0].Path);
            else ShowStart();
        };
    }

    private Session S => Session.Current;

    public void OnShown() => Keyboard.Focus(FileGrid);

    // ---------- Baum ----------

    private void BuildRoots()
    {
        _roots.Clear();
        var datasets = S.Datasets.Where(d => d.Name.Contains('/')).ToList(); // Pool-Wurzeln ausblenden
        foreach (var ds in datasets)
        {
            var hasParent = datasets.Any(o => o != ds && NasPath.IsUnder(ds.Mountpoint, o.Mountpoint));
            if (!hasParent) _roots.Add(FolderNode.ForDataset(ds));
        }
        if (_roots.Count == 0)
            foreach (var ds in S.Datasets) _roots.Add(FolderNode.ForDataset(ds));
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree || e.NewValue is not FolderNode node) return;
        if (node.Path != _currentPath) OpenFolder(node.Path);
    }

    /// <summary>Klappt den Baum bis zum aktuellen Ordner auf.</summary>
    private async Task SyncTreeAsync(string path)
    {
        var root = _roots.Where(r => NasPath.IsUnder(path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
        if (root == null) return;
        _syncingTree = true;
        try
        {
            var node = root;
            foreach (var step in NasPath.Chain(root.Path, path).Skip(1))
            {
                await node.LoadChildrenAsync();
                node.IsExpanded = true;
                var next = node.Children.FirstOrDefault(c => c.Path == step);
                if (next == null) break;
                node = next;
            }
            node.IsSelected = true;
        }
        catch (Exception) { /* nur Komfort */ }
        finally
        {
            _syncingTree = false;
        }
    }

    // ---------- Navigation ----------

    public void OpenFolder(string path) => _ = OpenFolderAsync(path, true);

    private async Task OpenFolderAsync(string path, bool addHistory)
    {
        if (!Rights.ConfirmLeave()) return;
        path = NasPath.Normalize(path);
        if (addHistory && _currentPath != null && _currentPath != path) _history.Push(_currentPath);
        _currentPath = path;
        S.Settings.LastFolder = path;
        S.Settings.Save();
        StartPanel.Visibility = Visibility.Collapsed;

        BuildBreadcrumb(path);
        BackButton.IsEnabled = _history.Count > 0;
        UpButton.IsEnabled = S.Datasets.All(d => d.Mountpoint != path) && S.DatasetFor(path) != null;
        FilterBox.Text = "";

        _ = SyncTreeAsync(path);
        _ = Rights.LoadAsync(path, true, FolderDisplayName(path));
        await LoadListAsync();
    }

    private string FolderDisplayName(string path)
    {
        var ds = S.Datasets.FirstOrDefault(d => d.Mountpoint == path);
        return ds != null ? ds.ShortName : NasPath.Name(path);
    }

    private void BuildBreadcrumb(string path)
    {
        var ds = _roots.Where(r => NasPath.IsUnder(path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
        var start = ds?.Path ?? S.DatasetFor(path)?.Mountpoint ?? path;
        Breadcrumb.ItemsSource = NasPath.Chain(start, path)
            .Select((p, i) => new Crumb(i == 0 ? FolderDisplayName(p) : NasPath.Name(p), p, i > 0))
            .ToList();
    }

    private async Task LoadListAsync(string? selectName = null)
    {
        if (_currentPath == null) return;
        var version = ++_listVersion;
        var path = _currentPath;
        ListLoading.Visibility = Visibility.Visible;
        EmptyFolder.Visibility = Visibility.Collapsed;
        _items.Clear();

        try
        {
            var entries = await S.Nas.ListDirAsync(path);
            if (version != _listVersion) return;
            foreach (var e in entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                _items.Add(new FileItem(e));

            EmptyFolder.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selectName != null && _items.FirstOrDefault(i => i.Name == selectName) is { } sel)
            {
                FileGrid.SelectedItem = sel;
                FileGrid.ScrollIntoView(sel);
            }
        }
        catch (Exception ex)
        {
            if (version == _listVersion) Ui.ShowError("Ordner konnte nicht gelesen werden", ex);
        }
        finally
        {
            if (version == _listVersion) ListLoading.Visibility = Visibility.Collapsed;
        }

        await PrepareSmbAsync(path, version);
        _ = MarkOwnRightsAsync(path, version);
    }

    /// <summary>SMB-Verbindung für Dateiaktionen und Änderungsdatum.</summary>
    private async Task PrepareSmbAsync(string path, int version)
    {
        _currentUnc = null;
        string? unc = null;
        string? problem = null;
        try { unc = await S.UncForAsync(path); }
        catch (Exception ex) { problem = ex.Message; }
        if (version != _listVersion) return;

        _currentUnc = unc;
        NoShareBar.IsOpen = unc == null;
        if (problem != null)
        {
            NoShareBar.Title = "Kein Dateizugriff";
            NoShareBar.Message = problem;
            NoShareBar.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            NoShareBar.Title = "Nicht freigegeben";
            NoShareBar.Message = "Dieser Ordner liegt in keiner SMB-Freigabe. Rechte lassen sich trotzdem ändern, Dateiaktionen nur in freigegebenen Ordnern.";
            NoShareBar.Severity = InfoBarSeverity.Informational;
        }
        UpdateToolbar();
        if (unc == null) return;

        try
        {
            var dates = await Task.Run(() => new DirectoryInfo(unc).EnumerateFileSystemInfos()
                .ToDictionary(i => i.Name, i => i.LastWriteTime, StringComparer.Ordinal));
            if (version != _listVersion) return;
            foreach (var item in _items)
                if (dates.TryGetValue(item.Name, out var d)) item.Modified = d;
        }
        catch (Exception) { /* Datum ist nur Zusatzinfo */ }
    }

    /// <summary>Markiert Unterordner mit eigenen Rechten (Schild-Symbol).</summary>
    private async Task MarkOwnRightsAsync(string path, int version)
    {
        try
        {
            var parent = await S.Nas.GetAclAsync(path);
            var expected = AclRules.InheritedFor(parent.Aces, true);
            foreach (var item in _items.Where(i => i.IsDirectory && !i.IsDataset).ToList())
            {
                if (version != _listVersion) return;
                var acl = await S.Nas.GetAclAsync(item.Path);
                item.HasOwnRights = acl.IsNfs4 && (acl.Protected || AclRules.OwnAces(acl, expected).Count > 0);
            }
        }
        catch (Exception) { /* nur Markierung */ }
    }

    private void ShowStart()
    {
        StartPanel.Visibility = Visibility.Visible;
        Rights.Clear();
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && path != _currentPath) OpenFolder(path);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0) _ = OpenFolderAsync(_history.Pop(), false);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath != null && UpButton.IsEnabled) OpenFolder(NasPath.Parent(_currentPath));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? selectName = null)
    {
        await LoadListAsync(selectName);
        if (!Rights.HasUnsavedChanges) await Rights.ReloadAsync();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    // ---------- Auswahl ----------

    private List<FileItem> Selection => FileGrid.SelectedItems.OfType<FileItem>().ToList();

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateToolbar();
        var sel = Selection;
        if (sel.Count == 1)
        {
            if (Rights.CurrentPath != sel[0].Path && Rights.ConfirmLeave())
                _ = Rights.LoadAsync(sel[0].Path, sel[0].IsDirectory, sel[0].Name);
        }
        else if (sel.Count == 0 && _currentPath != null && Rights.CurrentPath != _currentPath && Rights.ConfirmLeave())
        {
            _ = Rights.LoadAsync(_currentPath, true, FolderDisplayName(_currentPath));
        }
    }

    private void UpdateToolbar()
    {
        var hasSmb = _currentUnc != null;
        var count = Selection.Count;
        NewFolderButton.IsEnabled = UploadButton.IsEnabled = hasSmb;
        SelectionTools.Visibility = count > 0 && hasSmb ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.IsEnabled = count == 1;
        OpenButton.IsEnabled = count == 1;
        PasteButton.Visibility = hasSmb && _clipboard.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GridMenu_Opened(object sender, RoutedEventArgs e)
    {
        var hasSmb = _currentUnc != null;
        var count = Selection.Count;
        MenuOpen.IsEnabled = count == 1;
        MenuRename.IsEnabled = hasSmb && count == 1;
        MenuCopy.IsEnabled = MenuCut.IsEnabled = MenuDelete.IsEnabled = hasSmb && count > 0;
        MenuPaste.IsEnabled = hasSmb && (_clipboard.Count > 0 || Clipboard.ContainsFileDropList());
        MenuNewFolder.IsEnabled = hasSmb;
        MenuExplorer.IsEnabled = hasSmb;
    }

    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileItem }) OpenSelected();
    }

    private void FileGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (e.Key)
        {
            case Key.Enter: OpenSelected(); break;
            case Key.F2: Rename_Click(sender, e); break;
            case Key.Delete: Delete_Click(sender, e); break;
            case Key.F5: Refresh_Click(sender, e); break;
            case Key.Back: Up_Click(sender, e); break;
            case Key.Left when alt: Back_Click(sender, e); break;
            case Key.C when ctrl: Copy_Click(sender, e); break;
            case Key.X when ctrl: Cut_Click(sender, e); break;
            case Key.V when ctrl: Paste_Click(sender, e); break;
            case Key.N when ctrl && shift: NewFolder_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- Dateiaktionen ----------

    private string UncOf(FileItem item) => Path.Combine(_currentUnc!, item.Name);

    private static string? ValidateName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '"', '*', ':', '<', '>', '?', '|', '\\', '/' }) >= 0)
            return "Diese Zeichen sind nicht erlaubt: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Der Name darf nicht mit Punkt oder Leerzeichen enden.";
        return null;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selection is not [var item]) return;
        if (item.IsDirectory)
        {
            OpenFolder(item.Path);
            return;
        }
        if (_currentUnc == null)
        {
            Ui.Info("Öffnen nicht möglich", "Die Datei liegt in keiner SMB-Freigabe.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(UncOf(item)) { UseShellExecute = true }); }
        catch (Exception ex) { Ui.ShowError("Datei konnte nicht geöffnet werden", ex); }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUnc == null) return;
        try
        {
            var arg = Selection is [var item] ? $"/select,\"{UncOf(item)}\"" : $"\"{_currentUnc}\"";
            Process.Start("explorer.exe", arg);
        }
        catch (Exception ex) { Ui.ShowError("Explorer konnte nicht geöffnet werden", ex); }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var text = Selection is [var item] && _currentUnc != null ? UncOf(item) : _currentUnc ?? _currentPath;
        if (text == null) return;
        Clipboard.SetText(text);
        Ui.Toast("Pfad kopiert.", InfoBarSeverity.Informational);
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUnc == null) return;
        var unc = _currentUnc;
        var name = Ui.Input("Neuer Ordner", "Name des neuen Ordners", "Neuer Ordner", "Anlegen", ValidateName);
        if (name == null) return;
        var ok = await Ui.RunAsync("Ordner wird angelegt …", _ => Task.Run(() => FileOps.CreateFolder(unc, name)), errorTitle: "Ordner konnte nicht angelegt werden");
        if (!ok) return;
        await Log("Ordner angelegt", NasPath.Combine(_currentPath!, name), $"Ordner „{name}“ angelegt");
        await LoadListAsync(name);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUnc == null || Selection is not [var item]) return;
        var name = Ui.Input("Umbenennen", $"Neuer Name für „{item.Name}“", item.Name, "Umbenennen", ValidateName, selectNameOnly: !item.IsDirectory);
        if (name == null || name == item.Name) return;
        var source = UncOf(item);
        var ok = await Ui.RunAsync("Wird umbenannt …", _ => Task.Run(() => FileOps.Rename(source, name)), errorTitle: "Umbenennen fehlgeschlagen");
        if (!ok) return;
        await Log("Umbenannt", item.Path, $"„{item.Name}“ umbenannt in „{name}“");
        await LoadListAsync(name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUnc == null) return;
        var sel = Selection;
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"„{sel[0].Name}“" : $"{sel.Count} Elemente";
        var folders = sel.Count(i => i.IsDirectory);
        var message = $"{what} endgültig löschen?" +
                      (folders > 0 ? " Ordner werden mit ihrem gesamten Inhalt gelöscht." : "") +
                      " Ein Papierkorb existiert nur, wenn er in der Freigabe aktiviert ist – sonst helfen nur Snapshots.";
        if (!Ui.Confirm("Löschen", message, "Löschen", true, string.Join("\n", sel.Select(i => i.Name)))) return;

        var paths = sel.Select(UncOf).ToList();
        var ok = await Ui.RunAsync("Wird gelöscht …", scope => FileOps.DeleteAsync(paths, scope.Progress, scope.Token), true, "Löschen fehlgeschlagen");
        await Log("Gelöscht", _currentPath!, $"{what} gelöscht", sel.Select(i => i.Path).ToList(), ok);
        await LoadListAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => SetClipboard(false);
    private void Cut_Click(object sender, RoutedEventArgs e) => SetClipboard(true);

    private void SetClipboard(bool cut)
    {
        if (_currentUnc == null || Selection.Count == 0) return;
        _clipboard = Selection.Select(UncOf).ToList();
        _clipboardIsCut = cut;
        if (!cut)
        {
            // Zusätzlich in die Windows-Zwischenablage – so kann man auch in den lokalen Explorer einfügen.
            var list = new StringCollection();
            list.AddRange(_clipboard.ToArray());
            try { Clipboard.SetFileDropList(list); } catch (Exception) { /* nicht kritisch */ }
        }
        UpdateToolbar();
        Ui.Toast($"{_clipboard.Count} Element(e) {(cut ? "ausgeschnitten" : "kopiert")} – im Zielordner „Einfügen“ wählen.", InfoBarSeverity.Informational);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUnc == null) return;
        var target = _currentUnc;
        List<string> sources;
        var cut = false;

        if (_clipboard.Count > 0)
        {
            sources = _clipboard;
            cut = _clipboardIsCut;
        }
        else if (Clipboard.ContainsFileDropList())
        {
            sources = Clipboard.GetFileDropList().Cast<string>().ToList();
        }
        else return;

        var ok = await Ui.RunAsync(cut ? "Wird verschoben …" : "Wird kopiert …", async scope =>
        {
            if (cut) await FileOps.MoveAsync(sources, target, scope.Progress, scope.Token);
            else await FileOps.CopyAsync(sources, target, scope.Progress, scope.Token);
        }, true, cut ? "Verschieben fehlgeschlagen" : "Kopieren fehlgeschlagen");

        await Log(cut ? "Verschoben" : "Kopiert", _currentPath!,
            $"{sources.Count} Element(e) {(cut ? "verschoben" : "kopiert")} nach „{NasPath.Name(_currentPath!)}“", sources, ok);
        if (ok && cut) _clipboard = new List<string>();
        await LoadListAsync();
        UpdateToolbar();
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (UploadButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = UploadButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Dateien hochladen" };
        if (dlg.ShowDialog() == true) await UploadAsync(dlg.FileNames);
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Ordner hochladen" };
        if (dlg.ShowDialog() == true) await UploadAsync(dlg.FolderNames);
    }

    private void FileGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _currentUnc != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileGrid_Drop(object sender, DragEventArgs e)
    {
        if (_currentUnc == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        await UploadAsync(files);
    }

    private async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_currentUnc == null || localPaths.Count == 0) return;
        var target = _currentUnc;
        var ok = await Ui.RunAsync("Wird hochgeladen …", scope => FileOps.CopyAsync(localPaths, target, scope.Progress, scope.Token), true, "Hochladen fehlgeschlagen");
        await Log("Hochgeladen", _currentPath!, $"{localPaths.Count} Element(e) nach „{NasPath.Name(_currentPath!)}“ hochgeladen",
            localPaths.Select(Path.GetFileName).OfType<string>().ToList(), ok);
        if (ok) Ui.Toast("Hochladen abgeschlossen.");
        await LoadListAsync();
    }

    private Task Log(string action, string target, string summary, List<string>? details = null, bool success = true)
    {
        var entry = new ProtocolEntry
        {
            Category = ProtocolCategory.Files,
            Action = success ? action : action + " (abgebrochen/fehlerhaft)",
            Target = target,
            Summary = success ? summary : summary + " – nicht vollständig",
            Details = details ?? new List<string>(),
        };
        return S.LogAsync(entry);
    }

    private async void OnRightsSaved(string path)
    {
        // Schild-Markierungen in Liste und Baum aktualisieren.
        if (_currentPath != null) await MarkOwnRightsAsync(_currentPath, _listVersion);
        if (FindNode(path) is not { IsDataset: false } node) return;
        try
        {
            var parent = await S.Nas.GetAclAsync(NasPath.Parent(path));
            var acl = await S.Nas.GetAclAsync(path);
            node.HasOwnRights = acl.IsNfs4 && (acl.Protected || AclRules.OwnAces(acl, AclRules.InheritedFor(parent.Aces, true)).Count > 0);
        }
        catch (Exception) { /* nur Markierung */ }
    }

    private FolderNode? FindNode(string path)
    {
        IEnumerable<FolderNode> Walk(IEnumerable<FolderNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));
        return Walk(_roots).FirstOrDefault(n => n.Path == path);
    }
}

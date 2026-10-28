using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Paths;
using NasZentrale.Core.TrueNas;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Models;

/// <summary>Ordner im Baum links. Unterordner werden erst beim Aufklappen geladen.</summary>
public sealed partial class FolderNode : ObservableObject
{
    private static readonly FolderNode Placeholder = new("…", "", false);
    private bool _loaded;

    public FolderNode(string name, string path, bool isDataset)
    {
        Name = name;
        Path = path;
        IsDataset = isDataset;
        if (path.Length > 0) Children.Add(Placeholder);
    }

    public string Name { get; }
    public string Path { get; }
    public bool IsDataset { get; }
    public ObservableCollection<FolderNode> Children { get; } = new();

    public SymbolRegular Icon => IsDataset ? SymbolRegular.Database24 : SymbolRegular.Folder24;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Ordner hat eigene Rechte (nicht nur geerbt) – wird mit einem Schild markiert.</summary>
    [ObservableProperty] private bool _hasOwnRights;
    [ObservableProperty] private bool _isNotShared;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = LoadChildrenAsync();
    }

    public async Task LoadChildrenAsync(bool force = false)
    {
        if (_loaded && !force) return;
        _loaded = true;
        try
        {
            var entries = await Session.Current.Nas.ListDirAsync(Path);
            var dirs = entries.Where(e => e.IsDirectory && !e.IsSymlink).ToList();
            Children.Clear();
            foreach (var d in dirs)
                Children.Add(new FolderNode(d.Name, d.Path, d.IsMountpoint) { IsNotShared = Session.Current.Mapper.ShareFor(d.Path) == null });
            _ = MarkOwnRightsAsync(Children.ToList(), this);
        }
        catch (Exception)
        {
            _loaded = false;
            Children.Clear();
        }
    }

    /// <summary>Prüft im Hintergrund, welche Unterordner eigene Rechte haben.</summary>
    private static async Task MarkOwnRightsAsync(IReadOnlyList<FolderNode> nodes, FolderNode parent)
    {
        try
        {
            var nas = Session.Current.Nas;
            var parentAcl = await nas.GetAclAsync(parent.Path);
            var expected = AclRules.InheritedFor(parentAcl.Aces, true);
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(nodes.Where(n => !n.IsDataset).Select(async n =>
            {
                await gate.WaitAsync();
                try
                {
                    var acl = await nas.GetAclAsync(n.Path);
                    n.HasOwnRights = acl.IsNfs4 && (acl.Protected || AclRules.OwnAces(acl, expected).Count > 0);
                }
                catch (Exception) { /* nur Markierung */ }
                finally { gate.Release(); }
            }));
        }
        catch (Exception) { /* nur Markierung */ }
    }

    public static FolderNode ForDataset(DatasetInfo ds) =>
        new(ds.ShortName, ds.Mountpoint, true) { IsNotShared = Session.Current.Mapper.ShareFor(ds.Mountpoint) == null };
}

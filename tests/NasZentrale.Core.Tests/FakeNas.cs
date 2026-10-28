using NasZentrale.Core.Acl;
using NasZentrale.Core.Paths;
using NasZentrale.Core.TrueNas;

namespace NasZentrale.Core.Tests;

/// <summary>
/// Simuliertes NAS-Dateisystem. Rekursives Setzen verhält sich wie TrueNAS (nfs4xdr_winacl clone):
/// Alle Unterobjekte erhalten nur noch die geerbten Einträge – eigene Rechte gehen verloren.
/// </summary>
public sealed class FakeNas : IAclBackend
{
    private sealed class Node
    {
        public bool IsDir;
        public bool IsMount;
        public NfsAcl Acl = new();
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    public int SetAclCalls { get; private set; }

    public FakeNas Dir(string path, NfsAcl? acl = null, bool mount = false)
    {
        _nodes[path] = new Node { IsDir = true, IsMount = mount, Acl = Prepare(path, acl ?? InheritFromParent(path, true)) };
        return this;
    }

    public FakeNas File(string path, NfsAcl? acl = null)
    {
        _nodes[path] = new Node { IsDir = false, Acl = Prepare(path, acl ?? InheritFromParent(path, false)) };
        return this;
    }

    private static NfsAcl Prepare(string path, NfsAcl acl)
    {
        var c = acl.Clone();
        c.Path = path;
        return c;
    }

    private NfsAcl InheritFromParent(string path, bool isDir) =>
        new() { Aces = AclRules.InheritedFor(_nodes[NasPath.Parent(path)].Acl.Aces, isDir) };

    public NfsAcl Acl(string path) => _nodes[path].Acl.Clone();

    public Task<NfsAcl> GetAclAsync(string path, CancellationToken ct = default) => Task.FromResult(Acl(path));

    public Task<IReadOnlyList<FsEntry>> ListDirAsync(string path, CancellationToken ct = default)
    {
        IReadOnlyList<FsEntry> list = _nodes
            .Where(kv => kv.Key != path && NasPath.Parent(kv.Key) == path)
            .Select(kv => new FsEntry(NasPath.Name(kv.Key), kv.Key, kv.Value.IsDir, false, 0, kv.Value.IsMount, false, 0, 0))
            .ToList();
        return Task.FromResult(list);
    }

    public Task SetAclAsync(NfsAcl acl, bool recursive, CancellationToken ct = default)
    {
        SetAclCalls++;
        _nodes[acl.Path].Acl = Prepare(acl.Path, acl);
        if (recursive) Propagate(acl.Path);
        return Task.CompletedTask;
    }

    private void Propagate(string path)
    {
        foreach (var child in _nodes.Keys.Where(k => k != path && NasPath.Parent(k) == path).ToList())
        {
            var node = _nodes[child];
            if (node.IsMount) continue;
            node.Acl = new NfsAcl { Path = child, Aces = AclRules.InheritedFor(_nodes[path].Acl.Aces, node.IsDir) };
            if (node.IsDir) Propagate(child);
        }
    }
}

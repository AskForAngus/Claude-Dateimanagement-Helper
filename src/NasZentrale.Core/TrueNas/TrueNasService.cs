using System.Text.Json.Nodes;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Accounts;
using NasZentrale.Core.Paths;

namespace NasZentrale.Core.TrueNas;

public enum LoginOutcome { Success, OtpRequired, Failed }

public sealed record CurrentUser(string Name, long Uid, IReadOnlyList<long> Gids, IReadOnlyList<string> Roles)
{
    public bool IsFullAdmin => Roles.Contains("FULL_ADMIN");
}

public sealed record DatasetInfo(string Name, string Mountpoint, string AclType, long Used, long Available)
{
    public string ShortName => Name.Contains('/') ? Name[(Name.LastIndexOf('/') + 1)..] : Name;
    public string Pool => Name.Split('/')[0];
    public bool IsNfs4 => AclType.StartsWith("NFS", StringComparison.OrdinalIgnoreCase);
}

public sealed record FsEntry(string Name, string Path, bool IsDirectory, bool IsSymlink, long Size, bool IsMountpoint, bool IsCtlDir, long Uid, long Gid);

public sealed record SmbShare(long Id, string Name, string Path, string Comment, bool Enabled, bool? AccessBasedEnumeration, bool? ReadOnly, JsonObject Raw)
{
    public SmbShareInfo Info => new(Id, Name, Path, Enabled);
}

public enum SharePermission { Read, Change, Full }

public sealed record ShareAce(string? Sid, string Who, string? IdType, long? Id, SharePermission Permission, bool Deny)
{
    public string PermissionLabel => Permission switch
    {
        SharePermission.Full => "Vollzugriff",
        SharePermission.Change => "Ändern",
        _ => "Lesen",
    };
}

/// <summary>Typisierte TrueNAS-Funktionen, die NAS-Zentrale benötigt.</summary>
public interface IAclBackend
{
    Task<NfsAcl> GetAclAsync(string path, CancellationToken ct = default);
    Task SetAclAsync(NfsAcl acl, bool recursive, CancellationToken ct = default);
    Task<IReadOnlyList<FsEntry>> ListDirAsync(string path, CancellationToken ct = default);
}

public sealed class TrueNasService : IAclBackend
{
    private readonly ITrueNasApi _api;

    public TrueNasService(ITrueNasApi api) => _api = api;

    public ITrueNasApi Api => _api;

    private Task<JsonNode?> Call(string method, params object?[] args) => _api.CallAsync(method, args);
    private Task<JsonNode?> CallCt(CancellationToken ct, string method, params object?[] args) => _api.CallAsync(method, args, ct);

    // ---------- Anmeldung ----------

    public async Task<LoginOutcome> LoginAsync(string username, string password)
    {
        try
        {
            var r = await Call("auth.login_ex", new JsonObject
            {
                ["mechanism"] = "PASSWORD_PLAIN",
                ["username"] = username,
                ["password"] = password,
            });
            return r.Str("response_type") switch
            {
                "SUCCESS" => LoginOutcome.Success,
                "OTP_REQUIRED" => LoginOutcome.OtpRequired,
                _ => LoginOutcome.Failed,
            };
        }
        catch (TrueNasException ex) when (ex.IsMethodNotFound)
        {
            var ok = await Call("auth.login", username, password);
            return ok is JsonValue v && v.TryGetValue<bool>(out var b) && b ? LoginOutcome.Success : LoginOutcome.Failed;
        }
    }

    public async Task<bool> LoginOtpAsync(string otp)
    {
        var r = await Call("auth.login_ex_continue", new JsonObject { ["mechanism"] = "OTP_TOKEN", ["otp_token"] = otp });
        return r.Str("response_type") == "SUCCESS";
    }

    public async Task<CurrentUser> GetCurrentUserAsync()
    {
        var me = await Call("auth.me");
        var gids = me?["pw_groups"] is JsonArray g ? g.Select(x => x!.GetValue<long>()).ToList() : new List<long>();
        if (me.Long("pw_gid") is { } primary && !gids.Contains(primary)) gids.Add(primary);
        var roles = me?["privilege"]?["roles"] is JsonArray r ? r.Select(x => x!.ToString()).ToList() : new List<string>();
        return new CurrentUser(me.Str("pw_name") ?? "?", me.Long("pw_uid") ?? -1, gids, roles);
    }

    public async Task<string> GetVersionAsync() => (await Call("system.info")).Str("version") ?? "";

    public async Task<string?> GetSmbWorkgroupAsync()
    {
        try { return (await Call("smb.config")).Str("workgroup"); }
        catch (TrueNasException) { return null; }
    }

    // ---------- Datasets & Dateisystem ----------

    public async Task<IReadOnlyList<DatasetInfo>> GetDatasetsAsync()
    {
        var r = await Call("pool.dataset.query",
            new JsonArray(new JsonArray("type", "=", "FILESYSTEM")),
            new JsonObject
            {
                ["extra"] = new JsonObject
                {
                    ["flat"] = true,
                    ["retrieve_children"] = true,
                    ["properties"] = new JsonArray("mountpoint", "acltype", "used", "available"),
                },
            });

        return r.Objects()
            .Select(d => new DatasetInfo(
                d.Str("name") ?? d.Str("id") ?? "",
                d.Str("mountpoint") ?? "",
                d["acltype"].Str("value") ?? d.Str("acltype") ?? "",
                d.Long("used") ?? 0,
                d.Long("available") ?? 0))
            .Where(d => IsUserDataset(d))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Systemdatasets (Apps, .system, Boot) ausblenden.</summary>
    public static bool IsUserDataset(DatasetInfo d)
    {
        if (!d.Mountpoint.StartsWith("/mnt/", StringComparison.Ordinal)) return false;
        var parts = d.Name.Split('/');
        return !parts.Any(p => p.StartsWith('.') || p.StartsWith("ix-", StringComparison.Ordinal) || p == "ix-applications");
    }

    public async Task<IReadOnlyList<FsEntry>> ListDirAsync(string path, CancellationToken ct = default)
    {
        var r = await CallCt(ct, "filesystem.listdir", path, new JsonArray(), new JsonObject { ["order_by"] = new JsonArray("name") });
        return r.Objects()
            .Select(e => new FsEntry(
                e.Str("name") ?? "",
                e.Str("path") ?? NasPath.Combine(path, e.Str("name") ?? ""),
                e.Str("type") == "DIRECTORY",
                e.Str("type") == "SYMLINK",
                e.Long("size") ?? 0,
                e.Bool("is_mountpoint"),
                e.Bool("is_ctldir"),
                e.Long("uid") ?? -1,
                e.Long("gid") ?? -1))
            .Where(e => !e.IsCtlDir)
            .ToList();
    }

    public async Task<NfsAcl> GetAclAsync(string path, CancellationToken ct = default)
    {
        var r = await CallCt(ct, "filesystem.getacl", path, false, true);
        var acl = AclJson.Parse(r!);
        if (string.IsNullOrEmpty(acl.Path)) acl.Path = path;
        return acl;
    }

    public async Task SetAclAsync(NfsAcl acl, bool recursive, CancellationToken ct = default)
    {
        await _api.CallJobAsync("filesystem.setacl", new object?[] { AclJson.ToSetAclPayload(acl, recursive) }, null, ct);
    }

    public async Task ChownAsync(string path, long? uid, long? gid, bool recursive)
    {
        var payload = new JsonObject
        {
            ["path"] = path,
            ["options"] = new JsonObject { ["recursive"] = recursive, ["traverse"] = false },
        };
        if (uid is { } u) payload["uid"] = u;
        if (gid is { } g) payload["gid"] = g;
        await _api.CallJobAsync("filesystem.chown", new object?[] { payload });
    }

    public async Task<DatasetInfo?> CreateDatasetAsync(string fullName)
    {
        await Call("pool.dataset.create", new JsonObject { ["name"] = fullName, ["share_type"] = "SMB" });
        return (await GetDatasetsAsync()).FirstOrDefault(d => d.Name == fullName);
    }

    // ---------- Benutzer & Gruppen ----------

    public async Task<IReadOnlyList<Principal>> GetPrincipalsAsync()
    {
        var list = new List<Principal>();
        var dsOptions = new JsonObject { ["extra"] = new JsonObject { ["search_dscache"] = true } };

        async Task<JsonNode?> TryQuery(string method, JsonArray filters, JsonObject? options)
        {
            try { return options == null ? await Call(method, filters) : await Call(method, filters, options); }
            catch (TrueNasException) { return null; }
        }

        var adUsers = await TryQuery("user.query", new JsonArray(new JsonArray("local", "=", false)), dsOptions);
        var localUsers = await TryQuery("user.query", new JsonArray(new JsonArray("local", "=", true), new JsonArray("builtin", "=", false)), null);
        foreach (var u in adUsers.Objects().Concat(localUsers.Objects()))
        {
            var name = u.Str("username");
            if (string.IsNullOrEmpty(name) || u.Long("uid") is not { } uid) continue;
            list.Add(new Principal(PrincipalKind.User, name, uid, u.Str("full_name"), u.Str("sid"), !u.Bool("local", true)));
        }

        var adGroups = await TryQuery("group.query", new JsonArray(new JsonArray("local", "=", false)), dsOptions);
        var localGroups = await TryQuery("group.query", new JsonArray(new JsonArray("local", "=", true)), null);
        foreach (var g in adGroups.Objects().Concat(localGroups.Objects()))
        {
            var name = g.Str("group") ?? g.Str("name");
            if (string.IsNullOrEmpty(name) || g.Long("gid") is not { } gid) continue;
            // Interne Systemgruppen ohne Bedeutung für Freigaben ausblenden, builtin_users/-administrators behalten.
            if (g.Bool("builtin") && !name.StartsWith("builtin_", StringComparison.Ordinal)) continue;
            list.Add(new Principal(PrincipalKind.Group, name, gid, null, g.Str("sid"), !g.Bool("local", true)));
        }

        return list
            .GroupBy(p => (p.Kind, p.Id))
            .Select(grp => grp.First())
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Sucht einen Namen direkt im AD (falls der Verzeichnis-Cache aus ist).</summary>
    public async Task<Principal?> ResolveAsync(string name)
    {
        try
        {
            var u = await Call("user.get_user_obj", new JsonObject { ["username"] = name });
            if (u.Long("pw_uid") is { } uid) return new Principal(PrincipalKind.User, u.Str("pw_name") ?? name, uid, u.Str("pw_gecos"), u.Str("sid"), true);
        }
        catch (TrueNasException) { }
        try
        {
            var g = await Call("group.get_group_obj", new JsonObject { ["groupname"] = name });
            if (g.Long("gr_gid") is { } gid) return new Principal(PrincipalKind.Group, g.Str("gr_name") ?? name, gid, null, g.Str("sid"), true);
        }
        catch (TrueNasException) { }
        return null;
    }

    /// <summary>Gruppen-IDs eines Benutzers (inkl. primärer Gruppe).</summary>
    public async Task<IReadOnlyList<long>> GetUserGroupIdsAsync(string username)
    {
        var u = await Call("user.get_user_obj", new JsonObject { ["username"] = username, ["get_groups"] = true });
        var ids = u?["grouplist"] is JsonArray a ? a.Select(x => x!.GetValue<long>()).ToList() : new List<long>();
        if (u.Long("pw_gid") is { } gid && !ids.Contains(gid)) ids.Add(gid);
        return ids;
    }

    // ---------- SMB-Freigaben ----------

    public async Task<IReadOnlyList<SmbShare>> GetSharesAsync()
    {
        var r = await Call("sharing.smb.query");
        return r.Objects().Select(ParseShare).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static SmbShare ParseShare(JsonObject s) => new(
        s.Long("id") ?? 0,
        s.Str("name") ?? "",
        s.Str("path") ?? "",
        s.Str("comment") ?? "",
        s.Bool("enabled", true),
        s.ContainsKey("abe") ? s.Bool("abe") : null,
        s.ContainsKey("ro") ? s.Bool("ro") : null,
        (JsonObject)s.DeepClone());

    public async Task<SmbShare> CreateShareAsync(string name, string path, string comment, bool enabled, bool? abe, bool? readOnly)
    {
        var payload = new JsonObject { ["name"] = name, ["path"] = path, ["comment"] = comment, ["enabled"] = enabled };
        if (abe is { } a) payload["abe"] = a;
        if (readOnly is { } ro) payload["ro"] = ro;
        var r = await Call("sharing.smb.create", payload);
        return ParseShare((JsonObject)r!);
    }

    public async Task<SmbShare> UpdateShareAsync(long id, string name, string path, string comment, bool enabled, bool? abe, bool? readOnly)
    {
        var payload = new JsonObject { ["name"] = name, ["path"] = path, ["comment"] = comment, ["enabled"] = enabled };
        if (abe is { } a) payload["abe"] = a;
        if (readOnly is { } ro) payload["ro"] = ro;
        var r = await Call("sharing.smb.update", id, payload);
        return ParseShare((JsonObject)r!);
    }

    public Task DeleteShareAsync(long id) => Call("sharing.smb.delete", id);

    public async Task<IReadOnlyList<ShareAce>> GetShareAclAsync(string shareName)
    {
        var r = await Call("sharing.smb.getacl", new JsonObject { ["share_name"] = shareName });
        return r?["share_acl"].Objects().Select(e =>
        {
            var whoId = e["ae_who_id"] as JsonObject;
            var who = e.Str("ae_who_str") ?? e.Str("ae_who_sid") ?? "?";
            if (e.Str("ae_who_sid") == "S-1-1-0") who = "Jeder";
            return new ShareAce(
                e.Str("ae_who_sid"),
                who,
                whoId.Str("id_type"),
                whoId.Long("id"),
                e.Str("ae_perm") switch { "FULL" => SharePermission.Full, "CHANGE" => SharePermission.Change, _ => SharePermission.Read },
                e.Str("ae_type") == "DENIED");
        }).ToList() ?? new List<ShareAce>();
    }

    public async Task SetShareAclAsync(string shareName, IEnumerable<ShareAce> entries)
    {
        var acl = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject
            {
                ["ae_perm"] = e.Permission switch { SharePermission.Full => "FULL", SharePermission.Change => "CHANGE", _ => "READ" },
                ["ae_type"] = e.Deny ? "DENIED" : "ALLOWED",
            };
            if (!string.IsNullOrEmpty(e.Sid)) o["ae_who_sid"] = e.Sid;
            else o["ae_who_id"] = new JsonObject { ["id_type"] = e.IdType ?? "USER", ["id"] = e.Id };
            acl.Add(o);
        }
        await Call("sharing.smb.setacl", new JsonObject { ["share_name"] = shareName, ["share_acl"] = acl });
    }
}

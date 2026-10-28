using System.Text.Json.Nodes;
using NasZentrale.Core.TrueNas;

namespace NasZentrale.Core.Acl;

/// <summary>Übersetzt zwischen TrueNAS-JSON (filesystem.getacl/setacl) und dem internen Modell.</summary>
public static class AclJson
{
    private static readonly (string Key, NfsPerm Perm)[] PermKeys =
    {
        ("READ_DATA", NfsPerm.ReadData),
        ("WRITE_DATA", NfsPerm.WriteData),
        ("APPEND_DATA", NfsPerm.AppendData),
        ("READ_NAMED_ATTRS", NfsPerm.ReadNamedAttrs),
        ("WRITE_NAMED_ATTRS", NfsPerm.WriteNamedAttrs),
        ("EXECUTE", NfsPerm.Execute),
        ("DELETE_CHILD", NfsPerm.DeleteChild),
        ("READ_ATTRIBUTES", NfsPerm.ReadAttributes),
        ("WRITE_ATTRIBUTES", NfsPerm.WriteAttributes),
        ("DELETE", NfsPerm.Delete),
        ("READ_ACL", NfsPerm.ReadAcl),
        ("WRITE_ACL", NfsPerm.WriteAcl),
        ("WRITE_OWNER", NfsPerm.WriteOwner),
        ("SYNCHRONIZE", NfsPerm.Synchronize),
    };

    private static readonly (string Key, NfsFlag Flag)[] FlagKeys =
    {
        ("FILE_INHERIT", NfsFlag.FileInherit),
        ("DIRECTORY_INHERIT", NfsFlag.DirectoryInherit),
        ("NO_PROPAGATE_INHERIT", NfsFlag.NoPropagateInherit),
        ("INHERIT_ONLY", NfsFlag.InheritOnly),
        ("INHERITED", NfsFlag.Inherited),
    };

    public static NfsAcl Parse(JsonNode node)
    {
        var acl = new NfsAcl
        {
            Path = node.Str("path") ?? "",
            Uid = node.Long("uid") ?? -1,
            Gid = node.Long("gid") ?? -1,
            OwnerName = node.Str("user"),
            OwnerGroupName = node.Str("group"),
            AclType = node.Str("acltype") ?? "NFS4",
            Trivial = node.Bool("trivial"),
        };

        if (node["aclflags"] is JsonObject flags)
        {
            acl.Protected = flags.Bool("protected");
            acl.AutoInherit = flags.Bool("autoinherit");
            acl.Defaulted = flags.Bool("defaulted");
        }

        if (acl.IsNfs4)
            acl.Aces = node["acl"].Objects().Select(ParseAce).ToList();
        return acl;
    }

    public static NfsAce ParseAce(JsonObject o)
    {
        var ace = new NfsAce
        {
            Tag = o.Str("tag") switch
            {
                "owner@" => AceTag.Owner,
                "group@" => AceTag.OwnerGroup,
                "everyone@" => AceTag.Everyone,
                "USER" => AceTag.User,
                _ => AceTag.Group,
            },
            Type = o.Str("type") == "DENY" ? AceType.Deny : AceType.Allow,
            Who = o.Str("who"),
        };
        var id = o.Long("id");
        ace.Id = ace.IsSpecial ? null : id;
        ace.Perms = ParsePerms(o["perms"]);
        ace.Flags = ParseFlags(o["flags"]);
        return ace;
    }

    private static NfsPerm ParsePerms(JsonNode? perms)
    {
        if (perms is not JsonObject p) return NfsPerm.None;
        if (p.Str("BASIC") is { } basic)
        {
            return basic switch
            {
                "FULL_CONTROL" => NfsPerm.FullControl,
                "MODIFY" => NfsPerm.Modify,
                "READ" => NfsPerm.Read,
                "TRAVERSE" => NfsPerm.Traverse,
                _ => NfsPerm.None,
            };
        }
        var result = NfsPerm.None;
        foreach (var (key, perm) in PermKeys)
            if (p.Bool(key)) result |= perm;
        return result;
    }

    private static NfsFlag ParseFlags(JsonNode? flags)
    {
        if (flags is not JsonObject f) return NfsFlag.None;
        if (f.Str("BASIC") is { } basic)
            return basic == "INHERIT" ? NfsFlag.FileInherit | NfsFlag.DirectoryInherit : NfsFlag.None;
        var result = NfsFlag.None;
        foreach (var (key, flag) in FlagKeys)
            if (f.Bool(key)) result |= flag;
        return result;
    }

    public static JsonObject AceToJson(NfsAce ace)
    {
        var perms = new JsonObject();
        foreach (var (key, perm) in PermKeys) perms[key] = ace.Perms.HasFlag(perm);
        var flags = new JsonObject();
        foreach (var (key, flag) in FlagKeys) flags[key] = ace.Flags.HasFlag(flag);

        return new JsonObject
        {
            ["tag"] = ace.Tag switch
            {
                AceTag.Owner => "owner@",
                AceTag.OwnerGroup => "group@",
                AceTag.Everyone => "everyone@",
                AceTag.User => "USER",
                _ => "GROUP",
            },
            ["id"] = ace.IsSpecial ? null : ace.Id,
            ["type"] = ace.Type == AceType.Deny ? "DENY" : "ALLOW",
            ["perms"] = perms,
            ["flags"] = flags,
        };
    }

    /// <summary>Parameter für filesystem.setacl. Besitzer bleiben unverändert.</summary>
    public static JsonObject ToSetAclPayload(NfsAcl acl, bool recursive)
    {
        return new JsonObject
        {
            ["path"] = acl.Path,
            ["dacl"] = new JsonArray(acl.Aces.Select(a => (JsonNode)AceToJson(a)).ToArray()),
            ["acltype"] = "NFS4",
            ["nfs41_flags"] = new JsonObject
            {
                ["autoinherit"] = acl.AutoInherit,
                ["protected"] = acl.Protected,
                ["defaulted"] = false,
            },
            ["options"] = new JsonObject
            {
                ["recursive"] = recursive,
                ["traverse"] = false,
                ["canonicalize"] = true,
                ["validate_effective_acl"] = false,
            },
        };
    }

    /// <summary>Speicherbare Form für Protokoll/Rückgängig.</summary>
    public static JsonObject ToStorage(NfsAcl acl)
    {
        return new JsonObject
        {
            ["path"] = acl.Path,
            ["uid"] = acl.Uid,
            ["gid"] = acl.Gid,
            ["user"] = acl.OwnerName,
            ["group"] = acl.OwnerGroupName,
            ["acltype"] = acl.AclType,
            ["aclflags"] = new JsonObject
            {
                ["protected"] = acl.Protected,
                ["autoinherit"] = acl.AutoInherit,
                ["defaulted"] = acl.Defaulted,
            },
            ["acl"] = new JsonArray(acl.Aces.Select(a =>
            {
                var j = AceToJson(a);
                j["who"] = a.Who;
                return (JsonNode)j;
            }).ToArray()),
        };
    }
}

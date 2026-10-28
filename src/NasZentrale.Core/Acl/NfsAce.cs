namespace NasZentrale.Core.Acl;

/// <summary>Die 14 NFSv4-Einzelrechte – identisch mit den Windows-„Erweiterten Berechtigungen“.</summary>
[Flags]
public enum NfsPerm
{
    None = 0,
    ReadData = 1 << 0,
    WriteData = 1 << 1,
    AppendData = 1 << 2,
    ReadNamedAttrs = 1 << 3,
    WriteNamedAttrs = 1 << 4,
    Execute = 1 << 5,
    DeleteChild = 1 << 6,
    ReadAttributes = 1 << 7,
    WriteAttributes = 1 << 8,
    Delete = 1 << 9,
    ReadAcl = 1 << 10,
    WriteAcl = 1 << 11,
    WriteOwner = 1 << 12,
    Synchronize = 1 << 13,

    FullControl = (1 << 14) - 1,
    Modify = FullControl & ~(WriteAcl | WriteOwner),
    Read = ReadData | ReadNamedAttrs | Execute | ReadAttributes | ReadAcl | Synchronize,
    Traverse = Execute | ReadNamedAttrs | ReadAttributes | ReadAcl | Synchronize,
}

[Flags]
public enum NfsFlag
{
    None = 0,
    FileInherit = 1 << 0,
    DirectoryInherit = 1 << 1,
    NoPropagateInherit = 1 << 2,
    InheritOnly = 1 << 3,
    Inherited = 1 << 4,

    InheritanceMask = FileInherit | DirectoryInherit | InheritOnly,
}

public enum AceTag
{
    /// <summary>owner@ – der jeweilige Besitzer.</summary>
    Owner,
    /// <summary>group@ – die jeweilige Besitzergruppe.</summary>
    OwnerGroup,
    /// <summary>everyone@ – Jeder.</summary>
    Everyone,
    User,
    Group,
}

public enum AceType { Allow, Deny }

public sealed class NfsAce
{
    public AceTag Tag { get; set; }
    public long? Id { get; set; }
    public string? Who { get; set; }
    public AceType Type { get; set; } = AceType.Allow;
    public NfsPerm Perms { get; set; }
    public NfsFlag Flags { get; set; }

    public bool IsInherited => Flags.HasFlag(NfsFlag.Inherited);
    public bool IsInheritOnly => Flags.HasFlag(NfsFlag.InheritOnly);
    public bool IsSpecial => Tag is AceTag.Owner or AceTag.OwnerGroup or AceTag.Everyone;

    public NfsAce Clone() => (NfsAce)MemberwiseClone();

    public bool SamePrincipal(NfsAce other) =>
        Tag == other.Tag && (IsSpecial || Id == other.Id);

    /// <summary>Gleich bis auf das „geerbt“-Kennzeichen.</summary>
    public bool EquivalentIgnoringInherited(NfsAce other) =>
        SamePrincipal(other) && Type == other.Type && Perms == other.Perms &&
        (Flags & ~NfsFlag.Inherited) == (other.Flags & ~NfsFlag.Inherited);

    public string DisplayName => Tag switch
    {
        AceTag.Owner => "Besitzer",
        AceTag.OwnerGroup => "Besitzergruppe",
        AceTag.Everyone => "Jeder",
        _ => string.IsNullOrEmpty(Who) ? $"#{Id}" : Who!,
    };

    public override string ToString() => $"{Type} {DisplayName} {Perms} [{Flags}]";
}

/// <summary>Vollständige Zugriffsliste eines Ordners oder einer Datei.</summary>
public sealed class NfsAcl
{
    public string Path { get; set; } = "";
    public long Uid { get; set; } = -1;
    public long Gid { get; set; } = -1;
    public string? OwnerName { get; set; }
    public string? OwnerGroupName { get; set; }
    /// <summary>NFS4, POSIX1E oder DISABLED.</summary>
    public string AclType { get; set; } = "NFS4";
    public bool Trivial { get; set; }
    /// <summary>Vererbung von oben deaktiviert (Windows: „Vererbung deaktivieren“).</summary>
    public bool Protected { get; set; }
    public bool AutoInherit { get; set; }
    public bool Defaulted { get; set; }
    public List<NfsAce> Aces { get; set; } = new();

    public bool IsNfs4 => AclType == "NFS4";

    public NfsAcl Clone()
    {
        var c = (NfsAcl)MemberwiseClone();
        c.Aces = Aces.Select(a => a.Clone()).ToList();
        return c;
    }

    public IEnumerable<NfsAce> ExplicitAces => Aces.Where(a => !a.IsInherited);
    public IEnumerable<NfsAce> InheritedAces => Aces.Where(a => a.IsInherited);
}

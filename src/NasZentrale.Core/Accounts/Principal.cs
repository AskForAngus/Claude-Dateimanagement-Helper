using NasZentrale.Core.Acl;

namespace NasZentrale.Core.Accounts;

public enum PrincipalKind { User, Group, Special }

/// <summary>Ein Benutzer oder eine Gruppe (aus dem AD oder lokal auf dem NAS).</summary>
public sealed record Principal(PrincipalKind Kind, string Name, long Id, string? FullName = null, string? Sid = null, bool IsDomain = false)
{
    public AceTag? SpecialTag { get; init; }

    public string Display => string.IsNullOrWhiteSpace(FullName) || FullName == Name ? Name : $"{FullName} ({Name})";

    public string KindLabel => Kind switch
    {
        PrincipalKind.User => IsDomain ? "AD-Benutzer" : "Lokaler Benutzer",
        PrincipalKind.Group => IsDomain ? "AD-Gruppe" : "Lokale Gruppe",
        _ => "Spezial",
    };

    public NfsAce ToAce(NfsPerm perms, NfsFlag flags, AceType type = AceType.Allow) => new()
    {
        Tag = SpecialTag ?? (Kind == PrincipalKind.User ? AceTag.User : AceTag.Group),
        Id = Kind == PrincipalKind.Special ? null : Id,
        Who = Name,
        Perms = perms,
        Flags = flags,
        Type = type,
    };

    public static readonly Principal Owner = new(PrincipalKind.Special, "Besitzer", -1, "Der jeweilige Besitzer (owner@)") { SpecialTag = AceTag.Owner };
    public static readonly Principal OwnerGroup = new(PrincipalKind.Special, "Besitzergruppe", -1, "Die jeweilige Besitzergruppe (group@)") { SpecialTag = AceTag.OwnerGroup };
    public static readonly Principal Everyone = new(PrincipalKind.Special, "Jeder", -1, "Alle Benutzer (everyone@)") { SpecialTag = AceTag.Everyone };
}

namespace NasZentrale.Core.Acl;

/// <summary>Zugriffsstufen, wie sie in Windows und TrueNAS als Vorlage existieren.</summary>
public enum AccessLevel { None, Traverse, Read, Modify, Full, Custom }

/// <summary>Windows-Auswahl „Anwenden auf“.</summary>
public enum ApplyTo
{
    ThisFolderSubfoldersFiles,
    ThisFolderOnly,
    ThisFolderSubfolders,
    ThisFolderFiles,
    SubfoldersFilesOnly,
    SubfoldersOnly,
    FilesOnly,
}

public static class AclRules
{
    public static readonly IReadOnlyList<(NfsPerm Perm, string Label, string Hint)> PermissionLabels = new[]
    {
        (NfsPerm.Execute, "Ordner durchsuchen / Datei ausführen", "In Unterordner wechseln, auch ohne Leserecht im Ordner"),
        (NfsPerm.ReadData, "Ordner auflisten / Daten lesen", "Inhalt eines Ordners sehen bzw. Dateien öffnen"),
        (NfsPerm.ReadAttributes, "Attribute lesen", "Größe, Datum, schreibgeschützt usw. lesen"),
        (NfsPerm.ReadNamedAttrs, "Erweiterte Attribute lesen", "Zusätzliche Metadaten lesen"),
        (NfsPerm.WriteData, "Dateien erstellen / Daten schreiben", "Neue Dateien anlegen bzw. Dateien ändern"),
        (NfsPerm.AppendData, "Ordner erstellen / Daten anhängen", "Neue Unterordner anlegen bzw. an Dateien anhängen"),
        (NfsPerm.WriteAttributes, "Attribute schreiben", "z. B. schreibgeschützt setzen"),
        (NfsPerm.WriteNamedAttrs, "Erweiterte Attribute schreiben", "Zusätzliche Metadaten ändern"),
        (NfsPerm.DeleteChild, "Unterordner und Dateien löschen", "Inhalte löschen, auch ohne Löschrecht am Objekt selbst"),
        (NfsPerm.Delete, "Löschen", "Das Objekt selbst löschen"),
        (NfsPerm.ReadAcl, "Berechtigungen lesen", "Die Rechte ansehen"),
        (NfsPerm.WriteAcl, "Berechtigungen ändern", "Die Rechte bearbeiten"),
        (NfsPerm.WriteOwner, "Besitz übernehmen", "Besitzer ändern"),
        (NfsPerm.Synchronize, "Synchronisieren", "Technisch nötig für Windows – normalerweise immer an"),
    };

    public static string Label(this AccessLevel level) => level switch
    {
        AccessLevel.Full => "Vollzugriff",
        AccessLevel.Modify => "Ändern",
        AccessLevel.Read => "Lesen",
        AccessLevel.Traverse => "Nur durchgehen",
        AccessLevel.Custom => "Benutzerdefiniert",
        _ => "Kein Zugriff",
    };

    public static string Label(this ApplyTo a) => a switch
    {
        ApplyTo.ThisFolderSubfoldersFiles => "Dieser Ordner, Unterordner und Dateien",
        ApplyTo.ThisFolderOnly => "Nur dieser Ordner",
        ApplyTo.ThisFolderSubfolders => "Dieser Ordner und Unterordner",
        ApplyTo.ThisFolderFiles => "Dieser Ordner und Dateien",
        ApplyTo.SubfoldersFilesOnly => "Nur Unterordner und Dateien",
        ApplyTo.SubfoldersOnly => "Nur Unterordner",
        ApplyTo.FilesOnly => "Nur Dateien",
        _ => a.ToString(),
    };

    public static NfsPerm Perms(this AccessLevel level) => level switch
    {
        AccessLevel.Full => NfsPerm.FullControl,
        AccessLevel.Modify => NfsPerm.Modify,
        AccessLevel.Read => NfsPerm.Read,
        AccessLevel.Traverse => NfsPerm.Traverse,
        _ => NfsPerm.None,
    };

    /// <summary>Exakte Vorlage, sonst „Benutzerdefiniert“.</summary>
    public static AccessLevel ExactLevel(NfsPerm p) => p switch
    {
        NfsPerm.FullControl => AccessLevel.Full,
        NfsPerm.Modify => AccessLevel.Modify,
        NfsPerm.Read => AccessLevel.Read,
        NfsPerm.Traverse => AccessLevel.Traverse,
        NfsPerm.None => AccessLevel.None,
        _ => AccessLevel.Custom,
    };

    /// <summary>Höchste Vorlage, die vollständig enthalten ist (für Übersichten).</summary>
    public static AccessLevel AtLeastLevel(NfsPerm p)
    {
        if ((p & NfsPerm.FullControl) == NfsPerm.FullControl) return AccessLevel.Full;
        if ((p & NfsPerm.Modify) == NfsPerm.Modify) return AccessLevel.Modify;
        if ((p & NfsPerm.Read) == NfsPerm.Read) return AccessLevel.Read;
        if ((p & NfsPerm.Traverse) == NfsPerm.Traverse) return AccessLevel.Traverse;
        return p == NfsPerm.None ? AccessLevel.None : AccessLevel.Custom;
    }

    public static ApplyTo ApplyToFromFlags(NfsFlag f)
    {
        var fi = f.HasFlag(NfsFlag.FileInherit);
        var di = f.HasFlag(NfsFlag.DirectoryInherit);
        var io = f.HasFlag(NfsFlag.InheritOnly);
        return (fi, di, io) switch
        {
            (true, true, false) => ApplyTo.ThisFolderSubfoldersFiles,
            (false, true, false) => ApplyTo.ThisFolderSubfolders,
            (true, false, false) => ApplyTo.ThisFolderFiles,
            (true, true, true) => ApplyTo.SubfoldersFilesOnly,
            (false, true, true) => ApplyTo.SubfoldersOnly,
            (true, false, true) => ApplyTo.FilesOnly,
            _ => ApplyTo.ThisFolderOnly,
        };
    }

    /// <summary>Setzt die Vererbungs-Flags neu; „geerbt“ und „nicht weitergeben“ bleiben erhalten.</summary>
    public static NfsFlag WithApplyTo(NfsFlag current, ApplyTo a)
    {
        var keep = current & (NfsFlag.Inherited | NfsFlag.NoPropagateInherit);
        var set = a switch
        {
            ApplyTo.ThisFolderSubfoldersFiles => NfsFlag.FileInherit | NfsFlag.DirectoryInherit,
            ApplyTo.ThisFolderSubfolders => NfsFlag.DirectoryInherit,
            ApplyTo.ThisFolderFiles => NfsFlag.FileInherit,
            ApplyTo.SubfoldersFilesOnly => NfsFlag.FileInherit | NfsFlag.DirectoryInherit | NfsFlag.InheritOnly,
            ApplyTo.SubfoldersOnly => NfsFlag.DirectoryInherit | NfsFlag.InheritOnly,
            ApplyTo.FilesOnly => NfsFlag.FileInherit | NfsFlag.InheritOnly,
            _ => NfsFlag.None,
        };
        if (set == NfsFlag.None) keep &= ~NfsFlag.NoPropagateInherit;
        return keep | set;
    }

    /// <summary>
    /// Berechnet, welche Einträge ein neues Kindobjekt von seinem Elternordner erbt
    /// (NFSv4/Windows-Regeln, wie ZFS mit aclinherit=passthrough).
    /// </summary>
    public static List<NfsAce> InheritedFor(IEnumerable<NfsAce> parentAces, bool isDirectory)
    {
        var result = new List<NfsAce>();
        foreach (var ace in parentAces)
        {
            var f = ace.Flags;
            var noPropagate = f.HasFlag(NfsFlag.NoPropagateInherit);
            NfsFlag? childFlags = null;

            if (isDirectory)
            {
                if (f.HasFlag(NfsFlag.DirectoryInherit))
                    childFlags = noPropagate
                        ? NfsFlag.Inherited
                        : NfsFlag.Inherited | (f & (NfsFlag.FileInherit | NfsFlag.DirectoryInherit));
                else if (f.HasFlag(NfsFlag.FileInherit) && !noPropagate)
                    childFlags = NfsFlag.Inherited | NfsFlag.FileInherit | NfsFlag.InheritOnly;
            }
            else if (f.HasFlag(NfsFlag.FileInherit))
            {
                childFlags = NfsFlag.Inherited;
            }

            if (childFlags is { } cf)
            {
                var c = ace.Clone();
                c.Flags = cf;
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>Ob zwei ACLs an Unterobjekte dasselbe vererben (dann muss nichts weitergegeben werden).</summary>
    public static bool SameInheritance(IEnumerable<NfsAce> a, IEnumerable<NfsAce> b)
    {
        var la = a.ToList();
        var lb = b.ToList();
        return SameSet(InheritedFor(la, true), InheritedFor(lb, true)) &&
               SameSet(InheritedFor(la, false), InheritedFor(lb, false));
    }

    /// <summary>Vergleich als Multimenge, Reihenfolge egal.</summary>
    public static bool SameSet(IReadOnlyCollection<NfsAce> a, IReadOnlyCollection<NfsAce> b)
    {
        if (a.Count != b.Count) return false;
        var remaining = b.ToList();
        foreach (var ace in a)
        {
            var idx = remaining.FindIndex(x => x.EquivalentIgnoringInherited(ace));
            if (idx < 0) return false;
            remaining.RemoveAt(idx);
        }
        return true;
    }

    /// <summary>
    /// Die „eigenen“ Einträge eines Ordners: alles, was nicht aus dem Elternordner stammt.
    /// Einträge ohne Geerbt-Kennzeichen, die aber genau der Vererbung entsprechen (z. B. von alten Werkzeugen
    /// kopiert), zählen als geerbt – sonst würden Änderungen oben dort nie ankommen.
    /// </summary>
    public static List<NfsAce> OwnAces(NfsAcl acl, IReadOnlyList<NfsAce>? expectedFromParent)
    {
        if (acl.Protected) return acl.Aces.Select(a => a.Clone()).ToList();
        var expected = expectedFromParent?.ToList() ?? new List<NfsAce>();
        var own = new List<NfsAce>();
        foreach (var ace in acl.Aces)
        {
            if (ace.IsInherited) continue;
            var idx = expected.FindIndex(x => x.EquivalentIgnoringInherited(ace));
            if (idx >= 0) { expected.RemoveAt(idx); continue; }
            own.Add(ace.Clone());
        }
        return own;
    }

    /// <summary>NFSv4-Auswertung: welche Rechte hat ein Benutzer mit diesen Gruppen tatsächlich?</summary>
    public static NfsPerm Evaluate(NfsAcl acl, long uid, IReadOnlyCollection<long> gids)
    {
        NfsPerm granted = NfsPerm.None, denied = NfsPerm.None;
        foreach (var ace in acl.Aces)
        {
            if (ace.IsInheritOnly || !Applies(ace, acl, uid, gids)) continue;
            var open = ace.Perms & ~(granted | denied);
            if (ace.Type == AceType.Allow) granted |= open; else denied |= open;
        }
        return granted;
    }

    /// <summary>Rechte für eine Gruppe als Ganzes (Mitglied dieser Gruppe ohne weitere Sonderrechte).</summary>
    public static NfsPerm EvaluateGroup(NfsAcl acl, long gid) => Evaluate(acl, long.MinValue, new[] { gid });

    private static bool Applies(NfsAce ace, NfsAcl acl, long uid, IReadOnlyCollection<long> gids) => ace.Tag switch
    {
        AceTag.Everyone => true,
        AceTag.Owner => uid == acl.Uid,
        AceTag.OwnerGroup => gids.Contains(acl.Gid),
        AceTag.User => ace.Id == uid,
        AceTag.Group => ace.Id is { } id && gids.Contains(id),
        _ => false,
    };

    /// <summary>Kurztext für die Rechte eines Eintrags, z. B. „Ändern“ oder „Benutzerdefiniert (7 von 14)“.</summary>
    public static string Summary(NfsPerm perms)
    {
        var level = ExactLevel(perms);
        if (level != AccessLevel.Custom) return level.Label();
        var count = PermissionLabels.Count(p => perms.HasFlag(p.Perm));
        return $"Benutzerdefiniert ({count} von 14)";
    }
}

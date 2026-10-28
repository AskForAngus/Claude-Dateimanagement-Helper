using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NasZentrale.Core.Acl;
using Wpf.Ui.Controls;

namespace NasZentrale.Models;

/// <summary>Ein Einzelrecht als Häkchen im Editor.</summary>
public sealed partial class PermOption : ObservableObject
{
    private readonly AceItem _owner;

    public PermOption(AceItem owner, NfsPerm perm, string label, string hint)
    {
        _owner = owner;
        Perm = perm;
        Label = label;
        Hint = hint;
    }

    public NfsPerm Perm { get; }
    public string Label { get; }
    public string Hint { get; }

    public bool IsChecked
    {
        get => _owner.Ace.Perms.HasFlag(Perm);
        set
        {
            if (value == IsChecked) return;
            _owner.Ace.Perms = value ? _owner.Ace.Perms | Perm : _owner.Ace.Perms & ~Perm;
            _owner.Changed();
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>Ein Eintrag der Zugriffsliste (Benutzer/Gruppe + Rechte) im Rechte-Bereich.</summary>
public sealed partial class AceItem : ObservableObject
{
    public static readonly IReadOnlyList<AccessLevel> Levels = new[]
    {
        AccessLevel.Full, AccessLevel.Modify, AccessLevel.Read, AccessLevel.Traverse, AccessLevel.Custom,
    };

    public static readonly IReadOnlyList<ApplyTo> ApplyOptions = Enum.GetValues<ApplyTo>();

    private readonly Action _onChanged;

    public AceItem(NfsAce ace, bool isDirectory, Action onChanged)
    {
        Ace = ace;
        IsDirectory = isDirectory;
        _onChanged = onChanged;
        Permissions = new ObservableCollection<PermOption>(
            AclRules.PermissionLabels.Select(p => new PermOption(this, p.Perm, p.Label, p.Hint)));
    }

    public NfsAce Ace { get; }
    public bool IsDirectory { get; }
    public ObservableCollection<PermOption> Permissions { get; }

    public bool IsInherited => Ace.IsInherited;
    public bool IsEditable => !Ace.IsInherited;
    public bool IsDeny => Ace.Type == AceType.Deny;
    public string Name => Ace.DisplayName;

    public SymbolRegular Icon => Ace.Tag switch
    {
        AceTag.User => SymbolRegular.Person24,
        AceTag.Owner => SymbolRegular.PersonStar24,
        AceTag.OwnerGroup => SymbolRegular.PeopleStar24,
        AceTag.Everyone => SymbolRegular.Globe24,
        _ => SymbolRegular.People24,
    };

    public string KindText => Ace.Tag switch
    {
        AceTag.User => "Benutzer",
        AceTag.Group => "Gruppe",
        AceTag.Owner => "Der jeweilige Besitzer eines Objekts",
        AceTag.OwnerGroup => "Die jeweilige Besitzergruppe eines Objekts",
        _ => "Alle Benutzer",
    };

    public string Summary => IsDirectory
        ? $"{AclRules.Summary(Ace.Perms)} · {AclRules.ApplyToFromFlags(Ace.Flags).Label()}"
        : AclRules.Summary(Ace.Perms);

    /// <summary>„Ursprung“ eines geerbten Eintrags – wird nachgeladen.</summary>
    [ObservableProperty] private string? _inheritedFrom;
    [ObservableProperty] private string? _inheritedFromPath;

    public bool IsAllow
    {
        get => Ace.Type == AceType.Allow;
        set
        {
            Ace.Type = value ? AceType.Allow : AceType.Deny;
            Changed();
        }
    }

    /// <summary>0 = Zulassen, 1 = Verweigern (für die Auswahlliste).</summary>
    public int TypeIndex
    {
        get => IsAllow ? 0 : 1;
        set => IsAllow = value == 0;
    }

    public AccessLevel Level
    {
        get => AclRules.ExactLevel(Ace.Perms) is var l && l == AccessLevel.None ? AccessLevel.Custom : l;
        set
        {
            if (value == AccessLevel.Custom || value == Level)
            {
                // „Benutzerdefiniert“ ergibt sich aus den Häkchen – Anzeige zurücksetzen.
                OnPropertyChanged(nameof(Level));
                return;
            }
            Ace.Perms = value.Perms();
            Changed();
        }
    }

    public ApplyTo ApplyTo
    {
        get => AclRules.ApplyToFromFlags(Ace.Flags);
        set
        {
            Ace.Flags = AclRules.WithApplyTo(Ace.Flags, value);
            Changed();
        }
    }

    public bool OnlyDirectChildren
    {
        get => Ace.Flags.HasFlag(NfsFlag.NoPropagateInherit);
        set
        {
            if (value && ApplyTo == ApplyTo.ThisFolderOnly) return;
            Ace.Flags = value ? Ace.Flags | NfsFlag.NoPropagateInherit : Ace.Flags & ~NfsFlag.NoPropagateInherit;
            Changed();
        }
    }

    public bool CanLimitPropagation => IsDirectory && ApplyTo != ApplyTo.ThisFolderOnly;

    public void Changed()
    {
        foreach (var p in Permissions) p.Refresh();
        OnPropertyChanged(string.Empty);
        _onChanged();
    }
}

using System.IO;
using NasZentrale.Core.Acl;
using NasZentrale.Core.Accounts;
using NasZentrale.Core.Paths;
using NasZentrale.Core.Protocol;

namespace NasZentrale.Services;

/// <summary>Richtet den gemeinsamen Protokollordner auf dem NAS ein.</summary>
public static class ProtocolSetup
{
    public const string FolderName = ".nas-zentrale";

    /// <summary>
    /// Verwendet einen vorhandenen „.nas-zentrale“-Ordner oder legt ihn im gewählten Ordner an
    /// (versteckt, nur für die Admin-Gruppe zugänglich). Lokale Einträge werden übernommen.
    /// </summary>
    public static async Task<string> SetupAsync(string chosenFolder, Principal adminGroup, IProgress<string> progress)
    {
        var s = Session.Current;
        var folder = NasPath.Name(chosenFolder) == FolderName ? chosenFolder : NasPath.Combine(chosenFolder, FolderName);
        var parentUnc = await s.UncForAsync(NasPath.Parent(folder))
                        ?? throw new IOException("Der gewählte Ordner liegt in keiner aktiven SMB-Freigabe. Bitte einen freigegebenen Ordner wählen.");
        var unc = Path.Combine(parentUnc, FolderName);

        var existed = Directory.Exists(unc);
        if (!existed)
        {
            progress.Report("Protokollordner wird angelegt …");
            var dir = Directory.CreateDirectory(unc);
            dir.Attributes |= FileAttributes.Hidden;

            progress.Report("Zugriff auf Admins beschränken …");
            var acl = await s.Nas.GetAclAsync(folder);
            if (acl.IsNfs4)
            {
                const NfsFlag inherit = NfsFlag.FileInherit | NfsFlag.DirectoryInherit;
                acl.Protected = true;
                acl.Aces = new List<NfsAce>
                {
                    Principal.Owner.ToAce(NfsPerm.FullControl, inherit),
                    adminGroup.ToAce(NfsPerm.Modify, inherit),
                };
                // Nicht selbst aussperren, falls man nicht in der gewählten Gruppe ist.
                if (!s.Me.Gids.Contains(adminGroup.Id))
                    acl.Aces.Add(new NfsAce { Tag = AceTag.User, Id = s.Me.Uid, Who = s.Me.Name, Perms = NfsPerm.Modify, Flags = inherit });
                await s.Nas.SetAclAsync(acl, true);
            }
        }

        // Bisher lokal gespeicherte Einträge ins gemeinsame Protokoll übernehmen.
        var local = new ProtocolStore(Path.Combine(AppSettings.AppDataFolder, "Protokoll"));
        var entries = await local.LoadAsync(120);
        if (entries.Count > 0)
        {
            progress.Report("Bisherige Einträge werden übernommen …");
            var shared = new ProtocolStore(unc);
            foreach (var e in entries.OrderBy(e => e.Time)) await shared.AppendAsync(e);
            Directory.Move(local.Folder, local.Folder + "-uebernommen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        s.Settings.ProtocolFolder = folder;
        s.Settings.AdminGroup = adminGroup.Name;
        s.Settings.SetupDone = true;
        s.Settings.Save();
        await s.OpenProtocolAsync();

        if (!existed)
            await s.LogAsync(new ProtocolEntry
            {
                Category = ProtocolCategory.Rights,
                Action = "Protokoll eingerichtet",
                Target = folder,
                Summary = "Gemeinsames Protokoll eingerichtet",
                Details = { $"Zugriff nur für {adminGroup.Name}" },
            });
        return folder;
    }
}

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NasZentrale.Dialogs;
using NasZentrale.Services;

namespace NasZentrale.Views;

public partial class SettingsView : UserControl, IPage
{
    public SettingsView()
    {
        InitializeComponent();
        Fill();
    }

    private Session S => Session.Current;

    public void OnShown() => Fill();

    private void Fill()
    {
        HostText.Text = S.Host;
        VersionText.Text = S.Version;
        UserText.Text = S.Me.Name + (S.Me.IsFullAdmin ? "  (volle Admin-Rechte)" : "  (eingeschränkte Rechte – manche Änderungen werden abgelehnt)");
        SmbText.Text = $@"als {S.SmbUserName}";

        S.Settings.TrustedCertificates.TryGetValue(S.Host, out var thumb);
        CertText.Text = thumb ?? (S.Client.LastCertificateWasValid ? "Offiziell gültiges Zertifikat" : "–");
        ForgetCertButton.Visibility = thumb != null ? Visibility.Visible : Visibility.Collapsed;

        ProtocolText.Text = S.ProtocolIsShared
            ? $"Gemeinsam auf dem NAS: {S.Settings.ProtocolFolder}"
            : S.ProtocolProblem ?? "Noch nicht eingerichtet – das Protokoll wird nur auf diesem PC gespeichert.";
        AdminGroupText.Text = S.Settings.AdminGroup.Length > 0 ? $"Admin-Gruppe: {S.Settings.AdminGroup}" : "";

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutText.Text = $"Version {version?.ToString(3)} – verwaltet Freigaben, Ordner, Dateien und Rechte von TrueNAS SCALE an einem Ort.";
    }

    private void ForgetCert_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load();
        settings.TrustedCertificates.Remove(S.Host);
        settings.Save();
        S.Settings.TrustedCertificates.Remove(S.Host);
        Fill();
        Ui.Toast("Zertifikat vergessen – beim nächsten Anmelden wird erneut gefragt.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
    }

    private void SetupProtocol_Click(object sender, RoutedEventArgs e)
    {
        Ui.Prepare(new SetupDialog()).ShowDialog();
        Fill();
    }
}

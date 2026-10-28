using System.Windows;
using System.Windows.Input;
using NasZentrale.Core.TrueNas;
using NasZentrale.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NasZentrale.Views;

public partial class LoginWindow : FluentWindow
{
    private readonly AppSettings _settings = AppSettings.Load();

    public LoginWindow()
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            HostBox.Text = _settings.Host;
            UserBox.Text = _settings.Username;
            RememberBox.IsChecked = _settings.Remember;
            if (string.IsNullOrEmpty(HostBox.Text)) HostBox.Focus();
            else if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
            else PasswordBox.Focus();
        };
    }

    /// <summary>Gesetzt, wenn die Anmeldung erfolgreich war.</summary>
    public Session? Session { get; private set; }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        var password = PasswordBox.Password;
        if (host.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            ShowError("Bitte NAS-Adresse, Benutzername und Passwort eingeben.");
            return;
        }

        SetBusy("Verbinde mit dem NAS …");
        TrueNasClient? client = null;
        try
        {
            _settings.TrustedCertificates.TryGetValue(host, out var pinned);
            client = new TrueNasClient(host, pinned);
            if (!await ConnectWithCertificateCheckAsync(client, host)) { client = null; return; }

            SetBusy("Anmeldung wird geprüft …");
            var nas = new TrueNasService(client);
            var outcome = await nas.LoginAsync(user, password);
            if (outcome == LoginOutcome.OtpRequired)
            {
                var otp = Ui.Input("Zwei-Faktor-Code", "Gib den 6-stelligen Code aus deiner Authenticator-App ein.", "", "Bestätigen");
                outcome = otp != null && await nas.LoginOtpAsync(otp) ? LoginOutcome.Success : LoginOutcome.Failed;
            }
            if (outcome != LoginOutcome.Success)
            {
                ShowError("Anmeldung fehlgeschlagen. Prüfe Benutzername (z. B. FIRMA\\max) und Passwort. " +
                          "Anmelden können sich nur Mitglieder einer AD-Gruppe, der in TrueNAS Admin-Rechte gegeben wurden.");
                await client.DisposeAsync();
                client = null;
                return;
            }

            SetBusy("Lade Datasets, Freigaben und Benutzer …");
            var session = new Session(client, host, user, password);
            await session.InitializeAsync();

            if (!session.Me.IsFullAdmin)
                Ui.Info("Eingeschränkte Rechte",
                    "Dein Konto hat in TrueNAS keine vollen Admin-Rechte. Manche Aktionen (z. B. Rechte oder Freigaben ändern) werden deshalb abgelehnt.");

            _settings.Remember = RememberBox.IsChecked == true;
            _settings.Host = _settings.Remember ? host : "";
            _settings.Username = _settings.Remember ? user : "";
            _settings.Save();
            session.Settings.Host = _settings.Host;
            session.Settings.Username = _settings.Username;
            session.Settings.Remember = _settings.Remember;
            session.Settings.TrustedCertificates = _settings.TrustedCertificates;

            Session = session;
            client = null; // gehört jetzt der Sitzung
            Close();
        }
        catch (Exception ex)
        {
            ShowError(Ui.Describe(ex));
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (Session == null) SetIdle();
        }
    }

    /// <summary>Verbindet; bei selbstsigniertem Zertifikat einmalig nachfragen und den Fingerabdruck merken.</summary>
    private async Task<bool> ConnectWithCertificateCheckAsync(TrueNasClient client, string host)
    {
        try
        {
            await client.ConnectAsync();
            return true;
        }
        catch (CertificateNotTrustedException cert)
        {
            var known = _settings.TrustedCertificates.ContainsKey(host);
            var message = known
                ? "ACHTUNG: Das Zertifikat des NAS hat sich geändert. Das ist normal, wenn es erneuert wurde – " +
                  "andernfalls könnte jemand die Verbindung abfangen. Nur fortfahren, wenn du sicher bist."
                : "Das NAS verwendet ein eigenes (selbst ausgestelltes) Zertifikat. Das ist bei TrueNAS üblich. " +
                  "Vergleiche den Fingerabdruck bei Bedarf mit TrueNAS → Anmeldedaten → Zertifikate. Dem Zertifikat vertrauen?";
            if (!Ui.Confirm("Zertifikat bestätigen", message, "Vertrauen und verbinden", known,
                    $"Fingerabdruck (SHA-256):\n{cert.Thumbprint}\n\nAusgestellt für:\n{cert.Subject}"))
            {
                ShowError("Verbindung abgebrochen – Zertifikat nicht bestätigt.");
                await client.DisposeAsync();
                return false;
            }
            _settings.TrustedCertificates[host] = cert.Thumbprint;
            _settings.Save();
            client.PinnedThumbprint = cert.Thumbprint;
            await client.ConnectAsync();
            return true;
        }
    }

    private void SetBusy(string text)
    {
        ErrorBar.IsOpen = false;
        BusyText.Text = text;
        BusyPanel.Visibility = Visibility.Visible;
        Helpers.DialogSizing.Fit(this);
        LoginButton.IsEnabled = false;
    }

    private void SetIdle()
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = true;
    }

    private void ShowError(string text)
    {
        ErrorBar.Message = text;
        ErrorBar.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => Helpers.DialogSizing.Fit(this)), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NasZentrale.Services;
using NasZentrale.Views;
using Wpf.Ui.Appearance;

namespace NasZentrale;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        ApplyReadableAccent(ApplicationThemeManager.GetAppTheme());
        // Windows wechselt hell/dunkel → Bibliothek setzt Systemakzent neu, danach wieder unsere Farben.
        ApplicationThemeManager.Changed += (theme, _) => ApplyReadableAccent(theme);
        DispatcherUnhandledException += OnUnhandledException;
        ShowLogin();
    }

    /// <summary>Anmeldefenster zeigen; nach erfolgreicher Anmeldung das Hauptfenster.</summary>
    public void ShowLogin()
    {
        var login = new LoginWindow();
        MainWindow = login;
        login.Closed += (_, _) =>
        {
            if (login.Session == null)
            {
                Shutdown();
                return;
            }
            Session.Current = login.Session;
            var main = new MainWindow();
            MainWindow = main;
            main.Closed += async (_, _) =>
            {
                await Session.Current.CloseAsync();
                if (main.LogoutRequested) ShowLogin(); else Shutdown();
            };
            main.Show();
        };
        login.Show();
    }

    /// <summary>
    /// Feste Akzentfarben nach Windows-11-Standard (Blau). Die aus der Windows-Akzentfarbe abgeleiteten Töne
    /// sind bei dunklen Akzentfarben im dunklen Modus zu dunkel – dann stünden schwarze Häkchen und Knopftexte
    /// auf dunklem Grund.
    /// </summary>
    private static void ApplyReadableAccent(ApplicationTheme theme)
    {
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        if (theme == ApplicationTheme.Dark)
            ApplicationAccentColorManager.Apply(C("#0078D4"), C("#60CDFF"), C("#60CDFF"), C("#99EBFF"));
        else
            ApplicationAccentColorManager.Apply(C("#0078D4"), C("#005FB8"), C("#004C87"), C("#003E92"));
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Ui.ShowError("Unerwarteter Fehler", e.Exception);
    }
}

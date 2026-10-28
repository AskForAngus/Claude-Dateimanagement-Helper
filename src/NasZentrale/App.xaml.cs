using System.Windows;
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

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Ui.ShowError("Unerwarteter Fehler", e.Exception);
    }
}

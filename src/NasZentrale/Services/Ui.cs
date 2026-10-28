using System.IO;
using System.Windows;
using NasZentrale.Core.TrueNas;
using NasZentrale.Dialogs;
using NasZentrale.Views;
using Wpf.Ui.Controls;

namespace NasZentrale.Services;

/// <summary>Einheitliche Rückfragen, Fehlermeldungen und Hintergrund-Vorgänge.</summary>
public static class Ui
{
    public static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current.MainWindow;

    public static MainWindow? Main => Application.Current.MainWindow as MainWindow;

    public static T Prepare<T>(T window) where T : Window
    {
        var owner = Owner;
        if (owner != null && !ReferenceEquals(owner, window) && owner.IsVisible) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window;
    }

    public static bool Confirm(string title, string message, string primary = "OK", bool danger = false, string? details = null)
    {
        var d = Prepare(new MessageDialog(title, message, primary, "Abbrechen", null, details, danger,
            danger ? SymbolRegular.Warning24 : null));
        return d.ShowDialog() == true && d.Choice == DialogChoice.Primary;
    }

    public static DialogChoice Ask(string title, string message, string primary, string tertiary, string? details = null)
    {
        var d = Prepare(new MessageDialog(title, message, primary, "Abbrechen", tertiary, details));
        d.ShowDialog();
        return d.Choice;
    }

    public static void Info(string title, string message, string? details = null)
    {
        Prepare(new MessageDialog(title, message, "OK", null, null, details, false, SymbolRegular.Info24)).ShowDialog();
    }

    public static string? Input(string title, string label, string initial = "", string primary = "OK",
        Func<string, string?>? validate = null, bool selectNameOnly = false)
    {
        var d = Prepare(new InputDialog(title, label, initial, primary, validate, selectNameOnly));
        return d.ShowDialog() == true ? d.Value : null;
    }

    public static void ShowError(string context, Exception ex)
    {
        var inner = ex is AggregateException agg && agg.InnerExceptions.Count == 1 ? agg.InnerExceptions[0] : ex;
        Prepare(new MessageDialog(context, Describe(inner), "OK", null, null,
            inner is TrueNasException or IOException or UnauthorizedAccessException or TrueNasConnectionException ? null : inner.ToString(),
            false, SymbolRegular.ErrorCircle24)).ShowDialog();
    }

    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Keine Berechtigung für diese Aktion. Dein AD-Konto braucht auf der Freigabe die passenden Rechte.\n\n" + ex.Message,
        TrueNasException t when t.ErrorName == "EPERM" || t.ErrorName == "EACCES" => "TrueNAS verweigert den Zugriff: " + t.Message,
        TrueNasException t => "TrueNAS meldet: " + t.Message,
        TrueNasConnectionException => ex.Message,
        OperationCanceledException => "Der Vorgang wurde abgebrochen.",
        _ => ex.Message,
    };

    /// <summary>
    /// Führt einen längeren Vorgang mit Fortschrittsanzeige unten im Fenster aus.
    /// Fehler werden angezeigt; Rückgabe true bei Erfolg.
    /// </summary>
    public static async Task<bool> RunAsync(string text, Func<BusyScope, Task> work, bool cancellable = false, string? errorTitle = null)
    {
        var main = Main;
        using var scope = main?.BeginBusy(text, cancellable) ?? new BusyScope(null, text);
        try
        {
            await work(scope);
            return true;
        }
        catch (OperationCanceledException)
        {
            main?.ShowToast("Abgebrochen.", InfoBarSeverity.Warning);
            return false;
        }
        catch (Exception ex)
        {
            ShowError(errorTitle ?? "Das hat nicht geklappt", ex);
            return false;
        }
    }

    public static void Toast(string text, InfoBarSeverity severity = InfoBarSeverity.Success) => Main?.ShowToast(text, severity);
}

/// <summary>Ein laufender Vorgang: Fortschrittstext und Abbrechen.</summary>
public sealed class BusyScope : IDisposable
{
    private readonly Action<BusyScope>? _onDispose;
    private readonly CancellationTokenSource _cts = new();

    public BusyScope(Action<BusyScope>? onDispose, string text)
    {
        _onDispose = onDispose;
        Text = text;
        Progress = new Progress<string>(s => { Text = s; TextChanged?.Invoke(s); });
    }

    public string Text { get; private set; }
    public bool Cancellable { get; init; }
    public IProgress<string> Progress { get; }
    public CancellationToken Token => _cts.Token;
    public event Action<string>? TextChanged;

    public void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        _onDispose?.Invoke(this);
        _cts.Dispose();
    }
}

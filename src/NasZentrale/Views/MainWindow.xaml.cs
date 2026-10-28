using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NasZentrale.Dialogs;
using NasZentrale.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NasZentrale.Views;

public sealed class NavEntry
{
    public NavEntry(string title, SymbolRegular symbol, Func<UIElement> factory)
    {
        Title = title;
        Symbol = symbol;
        Factory = factory;
    }

    public string Title { get; }
    public SymbolRegular Symbol { get; }
    public Func<UIElement> Factory { get; }
    public UIElement? Page { get; set; }
}

public partial class MainWindow : FluentWindow
{
    private readonly List<BusyScope> _busy = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly NavEntry _files, _overview, _shares, _protocol, _settings;
    private bool _switchingNav;

    public MainWindow()
    {
        InitializeComponent();

        _files = new NavEntry("Dateien & Rechte", SymbolRegular.FolderPeople24, () => new FilesView());
        _overview = new NavEntry("Wer darf wo?", SymbolRegular.PeopleTeam24, () => new OverviewView());
        _shares = new NavEntry("Freigaben", SymbolRegular.Server24, () => new SharesView());
        _protocol = new NavEntry("Protokoll", SymbolRegular.History24, () => new ProtocolView());
        _settings = new NavEntry("Einstellungen", SymbolRegular.Settings24, () => new SettingsView());

        MainNav.ItemTemplate = FooterNav.ItemTemplate = BuildNavTemplate();
        MainNav.ItemsSource = new[] { _files, _overview, _shares, _protocol };
        FooterNav.ItemsSource = new[] { _settings };

        var s = Session.Current;
        UserName.Text = s.Me.Name;
        HostName.Text = $"{s.Host} · TrueNAS {s.Version.Replace("TrueNAS-", "")}";
        TitleBar.Title = $"NAS-Zentrale – {s.Host}";

        _toastTimer.Tick += (_, _) => { ToastBar.IsOpen = false; _toastTimer.Stop(); };
        s.Client.Disconnected += (_, _) => Dispatcher.BeginInvoke(new Action(OnDisconnected));

        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            MainNav.SelectedIndex = 0;
            if (!s.Settings.SetupDone) Dispatcher.BeginInvoke(new Action(() => Ui.Prepare(new SetupDialog()).ShowDialog()), DispatcherPriority.ApplicationIdle);
        };
    }

    public bool LogoutRequested { get; private set; }

    private static DataTemplate BuildNavTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new System.Windows.Data.Binding(nameof(NavEntry.Symbol)));
        icon.SetValue(FontSizeProperty, 20.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 12, 0));
        var text = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        text.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NavEntry.Title)));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingNav || sender is not ListBox list || list.SelectedItem is not NavEntry entry) return;
        _switchingNav = true;
        if (ReferenceEquals(list, MainNav)) FooterNav.SelectedItem = null; else MainNav.SelectedItem = null;
        _switchingNav = false;
        Show(entry);
    }

    private void Show(NavEntry entry)
    {
        entry.Page ??= entry.Factory();
        PageHost.Content = entry.Page;
        if (entry.Page is IPage page) page.OnShown();
    }

    private void Select(NavEntry entry)
    {
        var inMain = MainNav.Items.Contains(entry);
        (inMain ? MainNav : FooterNav).SelectedItem = entry;
        if (PageHost.Content != entry.Page || entry.Page == null) Show(entry);
    }

    // ---------- Navigation von anderen Seiten aus ----------

    public void OpenFolder(string nasPath)
    {
        Select(_files);
        ((FilesView)_files.Page!).OpenFolder(nasPath);
    }

    public void OpenProtocol() => Select(_protocol);

    public void InvalidateOverview()
    {
        if (_overview.Page is OverviewView o) o.MarkStale();
    }

    // ---------- Fortschritt & Meldungen ----------

    public BusyScope BeginBusy(string text, bool cancellable)
    {
        var scope = new BusyScope(EndBusy, text) { Cancellable = cancellable };
        scope.TextChanged += _ => UpdateBusy();
        _busy.Add(scope);
        UpdateBusy();
        return scope;
    }

    private void EndBusy(BusyScope scope)
    {
        _busy.Remove(scope);
        UpdateBusy();
    }

    private void UpdateBusy()
    {
        var current = _busy.LastOrDefault();
        BusyBar.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        if (current == null) return;
        BusyText.Text = current.Text;
        BusyCancel.Visibility = current.Cancellable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BusyCancel_Click(object sender, RoutedEventArgs e) => _busy.LastOrDefault()?.Cancel();

    public bool IsBusy => _busy.Count > 0;

    public void ShowToast(string text, InfoBarSeverity severity)
    {
        ToastBar.Severity = severity;
        ToastBar.Title = severity switch
        {
            InfoBarSeverity.Success => "Erledigt",
            InfoBarSeverity.Warning => "Hinweis",
            InfoBarSeverity.Error => "Fehler",
            _ => "Info",
        };
        ToastBar.Message = text;
        ToastBar.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- Verbindung ----------

    private bool _reconnecting;

    private async void OnDisconnected()
    {
        if (_reconnecting || LogoutRequested || !IsLoaded) return;
        _reconnecting = true;
        ConnectionBar.Message = "Die Verbindung zum NAS ist abgebrochen. Es wird neu verbunden …";
        ConnectionBar.IsOpen = true;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await Session.Current.ReconnectAsync())
            {
                ConnectionBar.IsOpen = false;
                _reconnecting = false;
                ShowToast("Verbindung wiederhergestellt.", InfoBarSeverity.Success);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)));
        }

        _reconnecting = false;
        ConnectionBar.Message = "Das NAS ist nicht erreichbar.";
        if (Ui.Confirm("Verbindung verloren", "Das NAS ist nicht erreichbar. Möchtest du dich neu anmelden?", "Neu anmelden"))
        {
            LogoutRequested = true;
            Close();
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy && !Ui.Confirm("Vorgang läuft", "Es läuft noch ein Vorgang. Trotzdem abmelden?", "Abmelden", true)) return;
        LogoutRequested = true;
        Close();
    }
}

/// <summary>Seiten können reagieren, wenn sie angezeigt werden.</summary>
public interface IPage
{
    void OnShown();
}

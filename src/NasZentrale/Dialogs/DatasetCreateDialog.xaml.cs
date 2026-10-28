using System.Text.RegularExpressions;
using System.Windows;
using NasZentrale.Services;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public partial class DatasetCreateDialog : FluentWindow
{
    public DatasetCreateDialog()
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        // Pools und vorhandene Datasets als mögliche Eltern.
        var parents = Session.Current.Datasets.Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        ParentBox.ItemsSource = parents;
        ParentBox.SelectedItem = parents.FirstOrDefault(p => !p.Contains('/')) ?? parents.FirstOrDefault();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string ParentDataset => ParentBox.SelectedItem as string ?? "";
    public string DatasetName => NameBox.Text.Trim();
    public string FullName => $"{ParentDataset}/{DatasetName}";
    public bool CreateShare => ShareBox.IsChecked == true;
    public string ShareName => DatasetName;

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        PreviewText.Text = DatasetName.Length == 0 ? "" : $"Wird angelegt als {FullName} (Ordner /mnt/{FullName})";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (ParentDataset.Length == 0) error = "Bitte auswählen, wo das Dataset angelegt wird.";
        else if (DatasetName.Length == 0) error = "Bitte einen Namen eingeben.";
        else if (!Regex.IsMatch(DatasetName, @"^[A-Za-z0-9][A-Za-z0-9_\-. ]*$"))
            error = "Erlaubt sind Buchstaben (ohne Umlaute), Ziffern, Leerzeichen sowie _ - . (am Anfang Buchstabe oder Ziffer).";
        else if (Session.Current.Datasets.Any(d => d.Name.Equals(FullName, StringComparison.OrdinalIgnoreCase)))
            error = "Dieses Dataset gibt es bereits.";
        else if (CreateShare && Session.Current.Shares.Any(s => s.Name.Equals(ShareName, StringComparison.OrdinalIgnoreCase)))
            error = "Eine Freigabe mit diesem Namen gibt es bereits – Haken entfernen oder anderen Namen wählen.";

        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        Helpers.DialogSizing.Fit(this);
        if (error == null) DialogResult = true;
    }
}

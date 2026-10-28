using System.Windows;
using NasZentrale.Helpers;
using NasZentrale.Models;
using NasZentrale.Services;
using Wpf.Ui.Controls;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace NasZentrale.Dialogs;

/// <summary>Ordner auf dem NAS auswählen (Datasets und Unterordner).</summary>
public partial class FolderPickerDialog : FluentWindow
{
    public FolderPickerDialog(string title, string? initial = null)
    {
        InitializeComponent();
        Title = TitleBar.Title = Heading.Text = title;
        Tree.ItemContainerStyle = StyleHelper.Derive(this, typeof(TreeViewItem),
            (TreeViewItem.IsExpandedProperty, nameof(FolderNode.IsExpanded)),
            (TreeViewItem.IsSelectedProperty, nameof(FolderNode.IsSelected)));
        Tree.ItemsSource = Session.Current.Datasets.Where(d => d.Name.Contains('/')).Select(FolderNode.ForDataset).ToList();
        SelectedText.Text = initial is { Length: > 0 } ? $"Aktuell: {initial}" : "Noch nichts ausgewählt";
    }

    public string? SelectedPath { get; private set; }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SelectedPath = (e.NewValue as FolderNode)?.Path;
        SelectedText.Text = SelectedPath ?? "";
        OkButton.IsEnabled = SelectedPath != null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System.Windows;
using Wpf.Ui.Controls;

namespace NasZentrale.Dialogs;

public partial class InputDialog : FluentWindow
{
    private readonly Func<string, string?>? _validate;

    public InputDialog(string title, string label, string initial, string primary, Func<string, string?>? validate, bool selectNameOnly)
    {
        InitializeComponent();
        Helpers.DialogSizing.Attach(this);
        _validate = validate;
        Title = title;
        TitleBar.Title = title;
        Heading.Text = title;
        LabelText.Text = label;
        OkButton.Content = primary;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            // Wie im Explorer: beim Umbenennen nur den Namen ohne Endung markieren.
            var dot = initial.LastIndexOf('.');
            if (selectNameOnly && dot > 0) Input.Select(0, dot); else Input.SelectAll();
        };
        Validate();
    }

    public string Value => Input.Text.Trim();

    private bool Validate()
    {
        var error = string.IsNullOrWhiteSpace(Input.Text) ? "" : _validate?.Invoke(Value);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(Input.Text) && error == null;
        if (IsLoaded) Helpers.DialogSizing.Fit(this);
        return OkButton.IsEnabled;
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Validate()) DialogResult = true;
    }
}

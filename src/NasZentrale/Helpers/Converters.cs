using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NasZentrale.Core.Acl;

namespace NasZentrale.Helpers;

/// <summary>Deutsche Bezeichnungen für Zugriffsstufen und „Anwenden auf“.</summary>
public sealed class LabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AccessLevel l => l.Label(),
        ApplyTo a => a.Label(),
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (value is string s) b = !string.IsNullOrEmpty(s);
        else if (value != null && value is not bool) b = true;
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

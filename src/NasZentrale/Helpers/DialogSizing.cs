using System.Windows;

namespace NasZentrale.Helpers;

/// <summary>
/// Passt die Fensterhöhe an den Inhalt an. „SizeToContent“ rechnet bei Fenstern ohne klassische Titelleiste
/// den Rahmen doppelt – daher wird hier selbst gemessen.
/// </summary>
public static class DialogSizing
{
    public static void Attach(Window window)
    {
        window.Loaded += (_, _) => Fit(window);
    }

    public static void Fit(Window window)
    {
        if (window.Content is not FrameworkElement content || window.ActualWidth <= 0) return;
        window.SizeToContent = SizeToContent.Manual;
        content.Measure(new Size(window.ActualWidth, double.PositiveInfinity));
        var height = Math.Ceiling(content.DesiredSize.Height) + 2;
        if (!double.IsNaN(window.MaxHeight) && height > window.MaxHeight) height = window.MaxHeight;
        window.Height = height;
    }
}

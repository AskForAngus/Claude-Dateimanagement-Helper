using System.Windows;
using System.Windows.Data;

namespace NasZentrale.Helpers;

public static class StyleHelper
{
    /// <summary>
    /// Erzeugt einen Stil auf Basis des aktuellen Theme-Stils (falls vorhanden) mit zusätzlichen Bindungen.
    /// So bleibt das Windows-11-Aussehen erhalten, ohne von internen Schlüsselnamen abzuhängen.
    /// </summary>
    public static Style Derive(FrameworkElement scope, Type targetType, params (DependencyProperty Property, string Path)[] bindings)
    {
        var style = new Style(targetType, scope.TryFindResource(targetType) as Style);
        foreach (var (property, path) in bindings)
            style.Setters.Add(new Setter(property, new Binding(path) { Mode = BindingMode.TwoWay }));
        return style;
    }
}

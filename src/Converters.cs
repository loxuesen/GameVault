using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GameVault;

/// <summary>Shows an element when the bound boolean is false, and collapses it when true.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    /// <summary>@return <see cref="Visibility.Visible"/> for false, otherwise collapsed</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Not supported; this converter is only used in one direction.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

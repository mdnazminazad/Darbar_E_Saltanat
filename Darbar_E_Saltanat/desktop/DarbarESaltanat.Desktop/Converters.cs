using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DarbarESaltanat.Domain.Enums;

namespace DarbarESaltanat.Desktop;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility v && v != Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Table status → tile colour (royal palette).</summary>
public sealed class TableStatusBrushConverter : IValueConverter
{
    private static readonly Brush Available = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush Reserved = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
    private static readonly Brush Occupied = new SolidColorBrush(Color.FromRgb(0x9B, 0x1C, 0x31));
    private static readonly Brush Billing = new SolidColorBrush(Color.FromRgb(0xD4, 0xA7, 0x2C));
    private static readonly Brush Cleaning = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        TableStatus.Available => Available,
        TableStatus.Reserved => Reserved,
        TableStatus.Occupied => Occupied,
        TableStatus.Billing => Billing,
        _ => Cleaning,
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

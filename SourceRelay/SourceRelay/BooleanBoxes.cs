using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SourceRelay;

public static class BooleanBoxes
{
    public static IValueConverter InverseConverter { get; } = new InverseBooleanVisibilityConverter();
    private sealed class InverseBooleanVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is false ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

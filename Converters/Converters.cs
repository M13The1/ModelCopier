using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ModelCopier.Converters
{
    /// <summary>true → Visible, false → Collapsed</summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }

    /// <summary>true → Collapsed, false → Visible  (inverse)</summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is Visibility.Visible);
    }

    /// <summary>non-null → Visible, null → Collapsed</summary>
    [ValueConversion(typeof(object), typeof(Visibility))]
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is null ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>null → Visible, non-null → Collapsed  (inverse)</summary>
    [ValueConversion(typeof(object), typeof(Visibility))]
    public sealed class NullToVisibilityInverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>DateTime? → "yyyy-MM-dd HH:mm" or "Never"</summary>
    [ValueConversion(typeof(DateTime?), typeof(string))]
    public sealed class DateTimeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is DateTime dt
                ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "Never";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

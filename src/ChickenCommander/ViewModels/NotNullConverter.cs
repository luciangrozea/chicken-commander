using System.Globalization;
using System.Windows.Data;

namespace ChickenCommander.ViewModels;

/// <summary>true when the bound value is not null (for DataTriggers).</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static readonly NotNullConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

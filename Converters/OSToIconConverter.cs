using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Material.Icons;

namespace Synclo.Converters;

public class OSToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string os)
            return MaterialIconKind.Laptop;

        // Normalize for easier matching
        var lower = os.ToLowerInvariant();

        if (lower.Contains("windows"))
            return MaterialIconKind.MicrosoftWindows;
        
        if (lower.Contains("osx") || lower.Contains("macos") || lower.Contains("darwin"))
            return MaterialIconKind.Apple;
        
        if (lower.Contains("linux"))
            return MaterialIconKind.Linux;
        
        if (lower.Contains("android"))
            return MaterialIconKind.Android;
        
        if (lower.Contains("ios") || lower.Contains("iphone") || lower.Contains("ipad"))
            return MaterialIconKind.AppleIos;
        
        if (lower.Contains("browser") || lower.Contains("web"))
            return MaterialIconKind.Web;

        return MaterialIconKind.Laptop;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

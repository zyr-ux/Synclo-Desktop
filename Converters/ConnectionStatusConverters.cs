using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;
using Synclo.ViewModels;

namespace Synclo.Converters;

public class ConnectionStatusToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConnectionStatus status)
        {
            return status switch
            {
                ConnectionStatus.Online => "Online",
                ConnectionStatus.Offline => "Offline",
                ConnectionStatus.NoInternet => "No Internet",
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class ConnectionStatusToIconKindConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConnectionStatus status)
        {
            return status switch
            {
                ConnectionStatus.Offline => MaterialIconKind.Close,
                ConnectionStatus.NoInternet => MaterialIconKind.AlertCircleOutline,
                _ => MaterialIconKind.Help
            };
        }
        return MaterialIconKind.Help;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class ConnectionStatusToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConnectionStatus status && parameter is string target)
        {
            return target switch
            {
                "Orb" => status == ConnectionStatus.Online,
                "Icon" => status != ConnectionStatus.Online,
                _ => false
            };
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class ConnectionStatusToBrushConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count > 0 && values[0] is ConnectionStatus status)
        {
            string key = status switch
            {
                ConnectionStatus.Online => "StatusOnline",
                ConnectionStatus.Offline => "StatusOffline",
                ConnectionStatus.NoInternet => "StatusNoInternet",
                _ => "Foreground"
            };

            if (Application.Current is IResourceNode resourceNode &&
                resourceNode.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) &&
                resource is IBrush brush)
            {
                return brush;
            }
        }
        return null;
    }
}

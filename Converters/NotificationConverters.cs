using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;

namespace Synclo.Converters;

public class NotificationTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NotificationType type)
        {
            return type switch
            {
                NotificationType.Success => MaterialIconKind.CheckCircleOutline,
                NotificationType.Error => MaterialIconKind.CloseCircleOutline,
                NotificationType.Warning => MaterialIconKind.AlertCircleOutline,
                NotificationType.Information => MaterialIconKind.InformationOutline,
                _ => MaterialIconKind.InformationOutline
            };
        }
        return MaterialIconKind.InformationOutline;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class NotificationTypeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NotificationType type)
        {
            string key = type switch
            {
                NotificationType.Success => "StatusOnline",
                NotificationType.Error => "StatusOffline",
                NotificationType.Warning => "StatusNoInternet",
                _ => "Foreground"
            };

            if (Application.Current is IResourceNode resourceNode &&
                resourceNode.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource))
            {
                if (resource is IBrush brush)
                {
                    return brush;
                }
                if (resource is Color color)
                {
                    return new SolidColorBrush(color);
                }
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

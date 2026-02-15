using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Material.Icons;
using Synclo.Models;

namespace Synclo.Converters;

public class ClipboardItemTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HistoryItemModel.ClipboardItemType itemType)
        {
            return itemType switch
            {
                HistoryItemModel.ClipboardItemType.Text => MaterialIconKind.FormatAlignLeft,
                HistoryItemModel.ClipboardItemType.Link => MaterialIconKind.Link,
                HistoryItemModel.ClipboardItemType.Image => MaterialIconKind.Image,
                HistoryItemModel.ClipboardItemType.Code => MaterialIconKind.CodeBraces,
                _ => MaterialIconKind.Help
            };
        }

        return MaterialIconKind.Help;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

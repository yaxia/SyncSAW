using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SyncSAW.Core;

namespace SyncSAW.App;

internal sealed class SyncStateDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SyncItemState state
            ? state switch
            {
                SyncItemState.InSync => "Synced",
                SyncItemState.Pending => "Pending",
                SyncItemState.LocalOnly => "Local only",
                SyncItemState.RemoteOnly => "Remote only",
                SyncItemState.Mixed => "Mixed",
                SyncItemState.Error => "Error",
                _ => state.ToString()
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class HierarchyIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * 18 : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class FileHierarchyIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "\uE8B7" : "\uE7C3";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class ExpansionIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "\uE70D" : "\uE76C";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long size)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)size;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:N0} {units[unit]}"
            : $"{amount:0.#} {units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
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
                SyncItemState.Error => "Error",
                _ => state.ToString()
            }
            : string.Empty;

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

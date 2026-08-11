using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SyncSAW.Core;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfSystemColors = System.Windows.SystemColors;

namespace SyncSAW.App;

internal static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmSystemBackdropType = 38;
    private const int MicaBackdrop = 2;

    public static bool Apply(AppTheme theme, Window? window = null)
    {
        var isDark = theme == AppTheme.Dark || theme == AppTheme.System && IsSystemDark();
        ApplyPalette(isDark);

        if (window is not null)
        {
            ApplyWindowBackdrop(window, isDark);
        }

        return isDark;
    }

    private static bool IsSystemDark()
    {
        if (SystemParameters.HighContrast)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static void ApplyPalette(bool isDark)
    {
        var resources = System.Windows.Application.Current.Resources;
        if (SystemParameters.HighContrast)
        {
            SetBrush(resources, "AppBackgroundBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "CardBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "CardSecondaryBrush", WpfSystemColors.ControlColor);
            SetBrush(resources, "InputBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "TextBrush", WpfSystemColors.WindowTextColor);
            SetBrush(resources, "MutedBrush", WpfSystemColors.GrayTextColor);
            SetBrush(resources, "BorderBrush", WpfSystemColors.ActiveBorderColor);
            SetBrush(resources, "HoverBrush", WpfSystemColors.ControlColor);
            SetBrush(resources, "SelectionBrush", WpfSystemColors.HighlightColor);
            SetBrush(resources, "SelectionTextBrush", WpfSystemColors.HighlightTextColor);
            SetBrush(resources, "AccentBrush", WpfSystemColors.HighlightColor);
            SetBrush(resources, "AccentHoverBrush", WpfSystemColors.HotTrackColor);
            SetBrush(resources, "OnAccentBrush", WpfSystemColors.HighlightTextColor);
            SetBrush(resources, "SuccessBrush", WpfSystemColors.WindowTextColor);
            SetBrush(resources, "SuccessBackgroundBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "WarningBrush", WpfSystemColors.WindowTextColor);
            SetBrush(resources, "WarningBackgroundBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "InfoBrush", WpfSystemColors.WindowTextColor);
            SetBrush(resources, "InfoBackgroundBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "DangerBrush", WpfSystemColors.WindowTextColor);
            SetBrush(resources, "DangerBackgroundBrush", WpfSystemColors.WindowColor);
            SetBrush(resources, "OverlayScrimBrush", "#00000000");
            return;
        }

        if (isDark)
        {
            SetBrush(resources, "AppBackgroundBrush", "#202020");
            SetBrush(resources, "CardBrush", "#2B2B2B");
            SetBrush(resources, "CardSecondaryBrush", "#252525");
            SetBrush(resources, "InputBrush", "#242424");
            SetBrush(resources, "TextBrush", "#F5F5F5");
            SetBrush(resources, "MutedBrush", "#B3B0AD");
            SetBrush(resources, "BorderBrush", "#414141");
            SetBrush(resources, "HoverBrush", "#383838");
            SetBrush(resources, "SelectionBrush", "#143A55");
            SetBrush(resources, "SelectionTextBrush", "#FFFFFF");
            SetBrush(resources, "AccentBrush", "#0F6CBD");
            SetBrush(resources, "AccentHoverBrush", "#2886D9");
            SetBrush(resources, "OnAccentBrush", "#FFFFFF");
            SetBrush(resources, "SuccessBrush", "#6CCB75");
            SetBrush(resources, "SuccessBackgroundBrush", "#183A1F");
            SetBrush(resources, "WarningBrush", "#FCE100");
            SetBrush(resources, "WarningBackgroundBrush", "#433519");
            SetBrush(resources, "InfoBrush", "#60CDFF");
            SetBrush(resources, "InfoBackgroundBrush", "#18384D");
            SetBrush(resources, "DangerBrush", "#FF99A4");
            SetBrush(resources, "DangerBackgroundBrush", "#442726");
            SetBrush(resources, "OverlayScrimBrush", "#66000000");
        }
        else
        {
            SetBrush(resources, "AppBackgroundBrush", "#F3F3F3");
            SetBrush(resources, "CardBrush", "#FFFFFF");
            SetBrush(resources, "CardSecondaryBrush", "#F8F8F8");
            SetBrush(resources, "InputBrush", "#FFFFFF");
            SetBrush(resources, "TextBrush", "#1B1A19");
            SetBrush(resources, "MutedBrush", "#605E5C");
            SetBrush(resources, "BorderBrush", "#E3E3E3");
            SetBrush(resources, "HoverBrush", "#F0F0F0");
            SetBrush(resources, "SelectionBrush", "#E5F1FB");
            SetBrush(resources, "SelectionTextBrush", "#1B1A19");
            SetBrush(resources, "AccentBrush", "#0F6CBD");
            SetBrush(resources, "AccentHoverBrush", "#115EA3");
            SetBrush(resources, "OnAccentBrush", "#FFFFFF");
            SetBrush(resources, "SuccessBrush", "#0E700E");
            SetBrush(resources, "SuccessBackgroundBrush", "#EFF9EF");
            SetBrush(resources, "WarningBrush", "#8A4F00");
            SetBrush(resources, "WarningBackgroundBrush", "#FFF4CE");
            SetBrush(resources, "InfoBrush", "#005A9E");
            SetBrush(resources, "InfoBackgroundBrush", "#E8F3FC");
            SetBrush(resources, "DangerBrush", "#C50F1F");
            SetBrush(resources, "DangerBackgroundBrush", "#FDE7E9");
            SetBrush(resources, "OverlayScrimBrush", "#66000000");
        }
    }

    private static void ApplyWindowBackdrop(Window window, bool isDark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var darkValue = isDark ? 1 : 0;
        var backdrop = MicaBackdrop;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref darkValue, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int));
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color) =>
        SetBrush(resources, key, (MediaColor)MediaColorConverter.ConvertFromString(color));

    private static void SetBrush(ResourceDictionary resources, string key, MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

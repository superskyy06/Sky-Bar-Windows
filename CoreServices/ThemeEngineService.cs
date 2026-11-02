using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SkyBar.CoreServices;

public static class ThemeEngineService
{
    public static void ApplyTheme(string themeMode, string accentHex)
    {
        bool useLight = themeMode switch
        {
            "Light" => true,
            "Dark" => false,
            _ => IsWindowsInLightMode()
        };

        var res = Application.Current.Resources;

        if (useLight)
        {
            SetSolid(res, "WindowSurfaceBrush", "#FAF5F5F7");
            SetSolid(res, "WindowStrokeBrush", "#1F000000");
            SetSolid(res, "CardBrush", "#FFFFFFFF");
            SetSolid(res, "CardStrokeBrush", "#12000000");
            SetSolid(res, "DividerBrush", "#14000000");
            SetSolid(res, "MacPrimaryTextBrush", "#FF1D1D1F");
            SetSolid(res, "MacSecondaryTextBrush", "#FF6E6E73");
            SetSolid(res, "ControlTrackOffBrush", "#FFE3E3E8");
            SetSolid(res, "ControlHoverBrush", "#0F000000");
            SetSolid(res, "SegmentSelectedBrush", "#FFFFFFFF");
            SetSolid(res, "MenuSurfaceBrush", "#FAFBFBFD");
            SetSolid(res, "DockIndicatorBrush", "#CC1D1D1F");
            SetGradient(res, "DockGlassBrush", "#C7FAFAFC", "#D9E6E6EA");
            SetGradient(res, "DockStrokeBrush", "#E6FFFFFF", "#21000000");

            // Kompatibilitas kunci lama
            SetSolid(res, "NotchSurfaceBrush", "#FAF5F5F7");
            SetSolid(res, "NotchBorderBrush", "#1F000000");
        }
        else
        {
            SetSolid(res, "WindowSurfaceBrush", "#F71E1E20");
            SetSolid(res, "WindowStrokeBrush", "#26FFFFFF");
            SetSolid(res, "CardBrush", "#FF2A2A2D");
            SetSolid(res, "CardStrokeBrush", "#12FFFFFF");
            SetSolid(res, "DividerBrush", "#1AFFFFFF");
            SetSolid(res, "MacPrimaryTextBrush", "#FFF5F5F7");
            SetSolid(res, "MacSecondaryTextBrush", "#FF98989D");
            SetSolid(res, "ControlTrackOffBrush", "#FF3A3A3C");
            SetSolid(res, "ControlHoverBrush", "#14FFFFFF");
            SetSolid(res, "SegmentSelectedBrush", "#FF636366");
            SetSolid(res, "MenuSurfaceBrush", "#F7252528");
            SetSolid(res, "DockIndicatorBrush", "#D9F5F5F7");
            SetGradient(res, "DockGlassBrush", "#C73A3A3F", "#D61E1E21");
            SetGradient(res, "DockStrokeBrush", "#47FFFFFF", "#14FFFFFF");

            SetSolid(res, "NotchSurfaceBrush", "#F71E1E20");
            SetSolid(res, "NotchBorderBrush", "#26FFFFFF");
        }

        try { SetSolid(res, "MacAccentBrush", accentHex); } catch { }
    }

    private static void SetSolid(ResourceDictionary res, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        res[key] = brush;
    }

    private static void SetGradient(ResourceDictionary res, string key, string topHex, string bottomHex)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(topHex),
            (Color)ColorConverter.ConvertFromString(bottomHex),
            90.0);
        brush.Freeze();
        res[key] = brush;
    }

    private static bool IsWindowsInLightMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 1;
        }
        catch { return false; }
    }
}

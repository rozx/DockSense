using System.Windows;
using System.Windows.Media;
using DockSense.Core;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DockSense.App.Services;

/// <summary>
/// DockSense's minimal look: neutral zinc grays, a monochrome accent, hairline borders. Applied on top of WPF-UI's
/// Fluent controls, which mostly draw translucent fills over the app background.
/// </summary>
public static class Appearance
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["ApplicationBackgroundBrush"] = "#161618",
        ["WindowBackground"] = "#161618",
        ["SolidBackgroundFillColorBaseBrush"] = "#161618",
        ["NavigationViewContentBackground"] = "#1B1B1E",
        ["NavigationViewContentGridBorderBrush"] = "#26262A",
        ["CardBackground"] = "#202023",
        ["CardBackgroundFillColorDefaultBrush"] = "#202023",
        ["CardBackgroundFillColorSecondaryBrush"] = "#242427",
        ["CardBackgroundPointerOver"] = "#242427",
        ["CardBorderBrush"] = "#2C2C31",
        ["CardStrokeColorDefaultBrush"] = "#2C2C31",
        ["ControlFillColorDefaultBrush"] = "#27272A",
        ["ControlFillColorSecondaryBrush"] = "#2E2E32",
        ["ControlFillColorTertiaryBrush"] = "#222225",
        ["ButtonBackground"] = "#27272A",
        ["ButtonBackgroundPointerOver"] = "#2E2E32",
        ["ButtonBackgroundPressed"] = "#222225",
        ["ComboBoxBackground"] = "#27272A",
        ["ComboBoxBackgroundPointerOver"] = "#2E2E32",
        ["ComboBoxDropDownBackground"] = "#232326",
        ["ComboBoxDropDownBorderBrush"] = "#34343A",
        ["ContextMenuBackground"] = "#232326",
        ["ContextMenuBorderBrush"] = "#34343A",
        ["FlyoutBackground"] = "#232326",
        ["ControlStrokeColorDefaultBrush"] = "#303035",
        ["ControlStrokeColorSecondaryBrush"] = "#3A3A40",
        ["DividerStrokeColorDefaultBrush"] = "#2C2C31",
        ["SeparatorBorderBrush"] = "#2C2C31",
        ["TextFillColorPrimaryBrush"] = "#F4F4F5",
        ["TextFillColorSecondaryBrush"] = "#A1A1AA",
        ["TextFillColorTertiaryBrush"] = "#71717A",
        ["TextFillColorDisabledBrush"] = "#52525B",
        ["InfoBarSuccessSeverityBackgroundBrush"] = "#202023",
        ["InfoBarWarningSeverityBackgroundBrush"] = "#202023",
        ["InfoBarInformationalSeverityBackgroundBrush"] = "#202023",
        ["InfoBarErrorSeverityBackgroundBrush"] = "#202023",
        ["InfoBarSuccessSeverityBorderBrush"] = "#2C2C31",
        ["InfoBarWarningSeverityBorderBrush"] = "#2C2C31",
        ["InfoBarInformationalSeverityBorderBrush"] = "#2C2C31",
        ["InfoBarErrorSeverityBorderBrush"] = "#2C2C31",
        ["InfoBarSuccessSeverityIconBackground"] = "#4ADE80",
        ["InfoBarWarningSeverityIconBackground"] = "#FBBF24",
        ["InfoBarInformationalSeverityIconBackground"] = "#A1A1AA",
        ["InfoBarErrorSeverityIconBackground"] = "#F87171",
        ["HyperlinkButtonForeground"] = "#D4D4D8",
        ["HyperlinkButtonForegroundPointerOver"] = "#FFFFFF",
        ["IconTileBrush"] = "#27272A",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["ApplicationBackgroundBrush"] = "#F4F4F5",
        ["WindowBackground"] = "#F4F4F5",
        ["SolidBackgroundFillColorBaseBrush"] = "#F4F4F5",
        ["NavigationViewContentBackground"] = "#FAFAFA",
        ["NavigationViewContentGridBorderBrush"] = "#E4E4E7",
        ["CardBackground"] = "#FFFFFF",
        ["CardBackgroundFillColorDefaultBrush"] = "#FFFFFF",
        ["CardBackgroundFillColorSecondaryBrush"] = "#FAFAFA",
        ["CardBackgroundPointerOver"] = "#FAFAFA",
        ["CardBorderBrush"] = "#E4E4E7",
        ["CardStrokeColorDefaultBrush"] = "#E4E4E7",
        ["ControlFillColorDefaultBrush"] = "#FFFFFF",
        ["ControlFillColorSecondaryBrush"] = "#F4F4F5",
        ["ControlFillColorTertiaryBrush"] = "#EFEFF1",
        ["ButtonBackground"] = "#FFFFFF",
        ["ButtonBackgroundPointerOver"] = "#F4F4F5",
        ["ButtonBackgroundPressed"] = "#EFEFF1",
        ["ComboBoxBackground"] = "#FFFFFF",
        ["ComboBoxBackgroundPointerOver"] = "#F4F4F5",
        ["ComboBoxDropDownBackground"] = "#FFFFFF",
        ["ComboBoxDropDownBorderBrush"] = "#E4E4E7",
        ["ContextMenuBackground"] = "#FFFFFF",
        ["ContextMenuBorderBrush"] = "#E4E4E7",
        ["FlyoutBackground"] = "#FFFFFF",
        ["ControlStrokeColorDefaultBrush"] = "#E4E4E7",
        ["ControlStrokeColorSecondaryBrush"] = "#D4D4D8",
        ["DividerStrokeColorDefaultBrush"] = "#E4E4E7",
        ["SeparatorBorderBrush"] = "#E4E4E7",
        ["TextFillColorPrimaryBrush"] = "#18181B",
        ["TextFillColorSecondaryBrush"] = "#52525B",
        ["TextFillColorTertiaryBrush"] = "#A1A1AA",
        ["TextFillColorDisabledBrush"] = "#D4D4D8",
        ["InfoBarSuccessSeverityBackgroundBrush"] = "#FFFFFF",
        ["InfoBarWarningSeverityBackgroundBrush"] = "#FFFFFF",
        ["InfoBarInformationalSeverityBackgroundBrush"] = "#FFFFFF",
        ["InfoBarErrorSeverityBackgroundBrush"] = "#FFFFFF",
        ["InfoBarSuccessSeverityBorderBrush"] = "#E4E4E7",
        ["InfoBarWarningSeverityBorderBrush"] = "#E4E4E7",
        ["InfoBarInformationalSeverityBorderBrush"] = "#E4E4E7",
        ["InfoBarErrorSeverityBorderBrush"] = "#E4E4E7",
        ["InfoBarSuccessSeverityIconBackground"] = "#16A34A",
        ["InfoBarWarningSeverityIconBackground"] = "#D97706",
        ["InfoBarInformationalSeverityIconBackground"] = "#71717A",
        ["InfoBarErrorSeverityIconBackground"] = "#DC2626",
        ["HyperlinkButtonForeground"] = "#3F3F46",
        ["HyperlinkButtonForegroundPointerOver"] = "#18181B",
        ["IconTileBrush"] = "#F4F4F5",
    };

    // Monochrome accent: primary buttons and toggles are near-white on dark, near-black on light.
    private static readonly Color DarkAccent = Color.FromRgb(0xE4, 0xE4, 0xE7);
    private static readonly Color LightAccent = Color.FromRgb(0x27, 0x27, 0x2A);

    private static AppTheme _theme = AppTheme.System;
    private static bool _watchingSystem;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        var dark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => !SystemUsesLightTheme(),
        };

        var baseTheme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(baseTheme, WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(dark ? DarkAccent : LightAccent, baseTheme);

        // Top-level application resources win over the merged WPF-UI theme dictionaries.
        var resources = Application.Current.Resources;
        foreach (var (key, hex) in dark ? Dark : Light)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }

        resources["ControlCornerRadius"] = new CornerRadius(6);
        resources["OverlayCornerRadius"] = new CornerRadius(8);

        if (!_watchingSystem)
        {
            _watchingSystem = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
                {
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
                }
            };
        }
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}

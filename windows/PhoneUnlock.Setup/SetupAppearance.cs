using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace PhoneUnlock.Setup;

internal static class SetupAppearance
{
    internal const string System = "system";
    internal const string Light = "light";
    internal const string Dark = "dark";

    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneUnlock",
        "setup-appearance.json");

    public static string Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsFile);
            var mode = JsonSerializer.Deserialize<AppearanceRecord>(json)?.Mode;
            return mode is Light or Dark ? mode : System;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return System;
        }
    }

    public static void Apply(Application application, string mode)
    {
        var useDark = mode == Dark || (mode == System && IsSystemDark());
        var palette = useDark
            ? new Palette("#08090B", "#191A1E", "#242427", "#345DD7", "#FFFFFF", "#C6C7D0", "#383941", "#8FE0B0", "#FFB4AB")
            : new Palette("#F4F6FA", "#FFFFFF", "#EEF1F6", "#315DD4", "#191A1E", "#5F6470", "#D8DEE8", "#247A49", "#B3261E");
        SetBrush(application, "WindowBrush", palette.Window);
        SetBrush(application, "CardBrush", palette.Card);
        SetBrush(application, "CardAltBrush", palette.CardAlt);
        SetBrush(application, "AccentBrush", palette.Accent);
        SetBrush(application, "TextBrush", palette.Text);
        SetBrush(application, "MutedBrush", palette.Muted);
        SetBrush(application, "StrokeBrush", palette.Stroke);
        SetBrush(application, "SuccessBrush", palette.Success);
        SetBrush(application, "ErrorBrush", palette.Error);
    }

    public static void Save(string mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppearanceRecord(mode)));
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Appearance is cosmetic; a read-only profile must not block setup.
        }
    }

    private static void SetBrush(Application application, string key, string color)
    {
        if (application.Resources[key] is SolidColorBrush brush)
        {
            brush.Color = (Color)ColorConverter.ConvertFromString(color);
        }
    }

    private static bool IsSystemDark() => SystemParameters.HighContrast
        ? true
        : Microsoft.Win32.Registry.CurrentUser
            .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            ?.GetValue("AppsUseLightTheme") is int value && value == 0;

    private sealed record AppearanceRecord(string Mode);

    private sealed record Palette(
        string Window,
        string Card,
        string CardAlt,
        string Accent,
        string Text,
        string Muted,
        string Stroke,
        string Success,
        string Error);
}

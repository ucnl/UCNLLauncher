using System.Text.RegularExpressions;

namespace UCNLLauncher.Services;

public static partial class AppSettings
{
    public const string SystemValue = "system";
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";

    private const string ThemeKey = "settings.theme";
    private const string LanguageKey = "settings.language";

    public static string Theme
    {
        get => NormalizeTheme(Read(ThemeKey));
        set => Write(ThemeKey, NormalizeTheme(value));
    }

    public static string Language
    {
        get => NormalizeLanguage(Read(LanguageKey));
        set => Write(LanguageKey, NormalizeLanguage(value));
    }

    public static bool IsValidTheme(string? value) =>
        value == SystemValue || value == ThemeLight || value == ThemeDark;

    public static bool IsValidLanguage(string? value) =>
        value == SystemValue || (value != null && LanguageCodeRegex().IsMatch(value));

    public static string NormalizeTheme(string? value) =>
        IsValidTheme(value) ? value! : SystemValue;

    public static string NormalizeLanguage(string? value) =>
        IsValidLanguage(value) ? value! : SystemValue;

    private static string? Read(string key)
    {
        try
        {
            return Preferences.Default.Get<string?>(key, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] read {key}: {ex.Message}");
            return null;
        }
    }

    private static void Write(string key, string value)
    {
        try
        {
            Preferences.Default.Set(key, value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppSettings] write {key}: {ex.Message}");
        }
    }

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodeRegex();
}

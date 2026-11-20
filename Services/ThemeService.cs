#if ANDROID
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Microsoft.Maui.Platform;
#endif

namespace UCNLLauncher.Services;

public sealed class ThemeService
{
    public static ThemeService Current { get; } = new();

    private Application? _application;
    private string? _lastEffectiveTheme;

    private ThemeService() { }

    public event EventHandler<string>? EffectiveThemeChanged;

    public string Preference => AppSettings.Theme;

    public string EffectiveTheme =>
        _application?.RequestedTheme == AppTheme.Dark ? AppSettings.ThemeDark : AppSettings.ThemeLight;

    public bool IsDark => EffectiveTheme == AppSettings.ThemeDark;

    public void Initialize(Application application)
    {
        if (_application != null) return;
        _application = application;
        application.RequestedThemeChanged += OnRequestedThemeChanged;
        ApplyPreference();
    }

    public bool SetPreference(string? value)
    {
        if (!AppSettings.IsValidTheme(value)) return false;
        AppSettings.Theme = value!;
        ApplyPreference();
        return true;
    }

    public void Refresh()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(Refresh);
            return;
        }

        ApplySystemBars();

        var effective = EffectiveTheme;
        if (effective == _lastEffectiveTheme) return;
        _lastEffectiveTheme = effective;
        EffectiveThemeChanged?.Invoke(this, effective);
    }

    public Color GetColor(string lightKey, string darkKey)
    {
        var key = IsDark ? darkKey : lightKey;
        if (_application?.Resources.TryGetValue(key, out var value) == true && value is Color color)
            return color;
        return IsDark ? Colors.Black : Colors.White;
    }

    private void ApplyPreference()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(ApplyPreference);
            return;
        }
        if (_application == null) return;

        var preference = AppSettings.Theme;
        _application.UserAppTheme = preference switch
        {
            AppSettings.ThemeLight => AppTheme.Light,
            AppSettings.ThemeDark => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };

#if ANDROID
        var nightMode = preference switch
        {
            AppSettings.ThemeLight => AppCompatDelegate.ModeNightNo,
            AppSettings.ThemeDark => AppCompatDelegate.ModeNightYes,
            _ => AppCompatDelegate.ModeNightFollowSystem
        };
        try
        {
            if (AppCompatDelegate.DefaultNightMode != nightMode)
                AppCompatDelegate.DefaultNightMode = nightMode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeService] night mode: {ex.Message}");
        }
#endif

        Refresh();
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => Refresh();

    private void ApplySystemBars()
    {
#if ANDROID
        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window == null) return;

            bool light = !IsDark;

            var statusColor = GetColor("UcnlStatusBarLight", "UcnlStatusBarDark");
            if (light && !OperatingSystem.IsAndroidVersionAtLeast(23))
                statusColor = GetColor("UcnlStatusBarLegacyLight", "UcnlStatusBarDark");

            var navigationColor = GetColor("UcnlNavigationBarLight", "UcnlNavigationBarDark");
            if (light && !OperatingSystem.IsAndroidVersionAtLeast(26))
                navigationColor = GetColor("UcnlNavigationBarLegacyLight", "UcnlNavigationBarDark");

            window.ClearFlags(Android.Views.WindowManagerFlags.TranslucentStatus);
            window.AddFlags(Android.Views.WindowManagerFlags.DrawsSystemBarBackgrounds);
            window.SetStatusBarColor(statusColor.ToPlatform());
            window.SetNavigationBarColor(navigationColor.ToPlatform());

            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller == null) return;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                controller.AppearanceLightStatusBars = light;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                controller.AppearanceLightNavigationBars = light;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeService] system bars: {ex.Message}");
        }
#endif
    }
}

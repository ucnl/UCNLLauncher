using UCNLLauncher.Services;

namespace UCNLLauncher;

public partial class MainPage : ContentPage
{
    internal readonly UsbService _usbService;
    private CompassService? _compassService;
    private bool _isLauncherLoaded;
    private bool _isAppLoaded;
    private bool _usbPollingStarted;
    private bool _isLaunching;
    private CancellationTokenSource? _launchCts;
    private bool _launcherSettingsOpen;
    private CancellationTokenSource? _readLoopCts;
    private CancellationTokenSource? _usbWatcherCts;
    private string _currentAppName = "";

    private Android.Locations.LocationManager? _locationManager;
    private HarmonyLocationListener? _locationListener;
    private CancellationTokenSource? _mauiGpsCts;

    private readonly PwaCacheService _cacheService;
    private GeolocationWebChromeClient? _webChromeClient;

    private bool _isSyncingAll = false;
    private readonly HashSet<string> _syncingApps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabledApps = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _appUrls = new()
    {
        { "uWaveSuite", "https://docs.unavlab.com/uWaveSuite/" },
        { "uWaver", "https://docs.unavlab.com/uWaver/" },
        { "RedPhoneDXConfig", "https://docs.unavlab.com/RedPhoneDXConfig-Web/" },
        { "uConsole", "https://docs.unavlab.com/uConsole/" },
        { "AzimuthWebSuite", "https://docs.unavlab.com/AzimuthWebSuite/" },
        { "AzimuthLBLX", "https://docs.unavlab.com/AzimuthLBLX/" },
        { "uGNSSMonitor", "https://docs.unavlab.com/uGNSS-Monitor/" }
    };

    public MainPage()
    {
        InitializeComponent();
        ApplyNativeTexts();
        _ = RequestLocationPermissionsAsync();
        _usbService = new UsbService();
        _cacheService = new PwaCacheService();

        MainWebView.Navigating += OnNavigating;
        MainWebView.Navigated += OnNavigated;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;

        ConfigureWebView();
        LoadLauncher();
    }

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        ThemeService.Current.EffectiveThemeChanged -= OnEffectiveThemeChanged;
        ThemeService.Current.EffectiveThemeChanged += OnEffectiveThemeChanged;
        ThemeService.Current.Refresh();
        UpdateWebViewBaseColor();

        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        LocalizationService.Current.Refresh();
        ApplyNativeTexts();
    }

    private void OnPageUnloaded(object? sender, EventArgs e)
    {
        ThemeService.Current.EffectiveThemeChanged -= OnEffectiveThemeChanged;
        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
    }

    private static string T(string key, params object[] args) => LocalizationService.Current.T(key, args);

    private void ApplyNativeTexts()
    {
        BtnBack.Text = "← " + T("native.back");
    }

    private void OnLanguageChanged(object? sender, string language)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ApplyNativeTexts();
            PushLauncherConfig();
        });
    }

    private void OnEffectiveThemeChanged(object? sender, string theme)
    {
        UpdateWebViewBaseColor();
        PushLauncherConfig();
    }

    public void PushLauncherConfig()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(PushLauncherConfig);
            return;
        }
        if (!IsLauncherPageActive()) return;

        string json;
        try
        {
            json = BuildLauncherConfigJson();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PushLauncherConfig] {ex.Message}");
            return;
        }
        _ = EvaluateLauncherScriptAsync($"window.ucnlApplyConfig&&window.ucnlApplyConfig({json})");
    }

    private static string BuildLauncherConfigJson()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteLauncherConfig(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteLauncherConfig(System.Text.Json.Utf8JsonWriter writer)
    {
        writer.WriteString("theme", ThemeService.Current.EffectiveTheme);
        writer.WriteString("themePref", ThemeService.Current.Preference);
        var localization = LocalizationService.Current;
        writer.WriteString("lang", localization.CurrentLanguage);
        writer.WriteString("langPref", localization.Preference);

        writer.WriteStartArray("languages");
        foreach (var language in localization.Languages)
        {
            writer.WriteStartObject();
            writer.WriteString("code", language.Code);
            writer.WriteString("name", language.Name);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartObject("strings");
        foreach (var pair in localization.Strings)
            writer.WriteString(pair.Key, pair.Value);
        writer.WriteEndObject();
    }

    private async Task EvaluateLauncherScriptAsync(string script)
    {
        try
        {
            await MainWebView.EvaluateJavaScriptAsync(script);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EvaluateLauncherScript] {ex.Message}");
        }
    }

    private static readonly HashSet<string> LauncherCommands = new(StringComparer.Ordinal)
    {
        "ready", "set_theme", "set_lang", "settings_open", "settings_closed"
    };

    private static bool TryParseLauncherCommand(string url, out string command, out string argument)
    {
        command = "";
        argument = "";
        const string prefix = "app://";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var body = url.Substring(prefix.Length);
        var queryIndex = body.IndexOf('?');
        var name = (queryIndex >= 0 ? body.Substring(0, queryIndex) : body).TrimEnd('/');
        if (!LauncherCommands.Contains(name)) return false;

        command = name;
        argument = queryIndex >= 0 ? Uri.UnescapeDataString(body.Substring(queryIndex + 1)) : "";
        return true;
    }

    private void HandleLauncherCommand(string command, string argument)
    {
        switch (command)
        {
            case "settings_open":
                _launcherSettingsOpen = true;
                return;
            case "settings_closed":
                _launcherSettingsOpen = false;
                return;
            case "set_theme":
                ThemeService.Current.SetPreference(argument);
                break;
            case "set_lang":
                LocalizationService.Current.SetPreference(argument);
                break;
        }
        PushLauncherConfig();
    }

    private void UpdateWebViewBaseColor()
    {
#if ANDROID
        if (MainWebView.Handler?.PlatformView is not Android.Webkit.WebView androidWebView) return;
        var color = string.IsNullOrEmpty(_currentAppName)
            ? ThemeService.Current.GetColor("UcnlBackgroundLight", "UcnlBackgroundDark")
            : ThemeService.Current.GetColor("UcnlPwaBackground", "UcnlPwaBackground");
        androidWebView.SetBackgroundColor(Microsoft.Maui.Platform.ColorExtensions.ToPlatform(color));
#endif
    }

    private async Task RequestLocationPermissionsAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (status != PermissionStatus.Granted)
                return;

            try
            {
                var bgStatus = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();
                if (bgStatus != PermissionStatus.Granted)
                {
                    bgStatus = await Permissions.RequestAsync<Permissions.LocationAlways>();
                }
            }
            catch { }
        }
        catch { }
    }

    private void ConfigureWebView()
    {
#if ANDROID
        if (MainWebView.Handler is Microsoft.Maui.Handlers.WebViewHandler existingHandler
            && existingHandler.PlatformView is Android.Webkit.WebView existingWv)
        {
            SetupAndroidWebView(existingHandler, existingWv);
        }
        MainWebView.HandlerChanged += (s, e) =>
        {
            if (MainWebView.Handler is Microsoft.Maui.Handlers.WebViewHandler handler
                && handler.PlatformView is Android.Webkit.WebView androidWebView)
            {
                SetupAndroidWebView(handler, androidWebView);
            }
        };
#endif
    }

#if ANDROID
    private void SetupAndroidWebView(Microsoft.Maui.Handlers.WebViewHandler handler, Android.Webkit.WebView androidWebView)
    {
        androidWebView.Settings.CacheMode = Android.Webkit.CacheModes.CacheElseNetwork;
        androidWebView.Settings.JavaScriptEnabled = true;
        androidWebView.Settings.DomStorageEnabled = true;
        androidWebView.Settings.DatabaseEnabled = true;
        androidWebView.Settings.AllowFileAccess = true;
        androidWebView.Settings.AllowFileAccessFromFileURLs = true;
        androidWebView.Settings.AllowUniversalAccessFromFileURLs = true;
        androidWebView.Settings.SetGeolocationEnabled(true);
        if (!OperatingSystem.IsAndroidVersionAtLeast(24))
            androidWebView.Settings.SetGeolocationDatabasePath(
                Android.App.Application.Context.FilesDir?.Path ?? "/data/data/com.unavlab.launcher/files");
        _webChromeClient = new GeolocationWebChromeClient(handler);
        androidWebView.SetWebChromeClient(_webChromeClient);
        androidWebView.SetWebViewClient(new LauncherWebViewClient(handler));
        androidWebView.AddJavascriptInterface(new NativeBridge(this), "NativeBridge");
        UpdateWebViewBaseColor();
    }
#endif

#if ANDROID
    protected override void OnAppearing()
    {
        base.OnAppearing();
        DeviceDisplay.KeepScreenOn = true;
    }

    internal void HandleFileChooserResult(int requestCode, Android.App.Result resultCode, Android.Content.Intent? data)
    {
        _webChromeClient?.HandleActivityResult(requestCode, resultCode, data);
    }

    public class GeolocationWebChromeClient : Microsoft.Maui.Platform.MauiWebChromeClient
    {
        private Android.Webkit.IValueCallback? _filePathCallback;
        internal const int FileChooserRequestCode = 4321;

        public GeolocationWebChromeClient(Microsoft.Maui.Handlers.IWebViewHandler handler) : base(handler) { }

        public override void OnGeolocationPermissionsShowPrompt(string? origin, Android.Webkit.GeolocationPermissions.ICallback? callback)
        {
            callback?.Invoke(origin, true, false);
        }

        public override bool OnShowFileChooser(
            Android.Webkit.WebView? webView,
            Android.Webkit.IValueCallback? filePathCallback,
            Android.Webkit.WebChromeClient.FileChooserParams? fileChooserParams)
        {
            System.Diagnostics.Debug.WriteLine("[FileChooser] OnShowFileChooser called");

            _filePathCallback = filePathCallback;

            var intent = fileChooserParams?.CreateIntent();
            if (intent == null)
            {
                System.Diagnostics.Debug.WriteLine("[FileChooser] CreateIntent returned null");
                _filePathCallback = null;
                return false;
            }

            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                activity?.StartActivityForResult(intent, FileChooserRequestCode);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FileChooser] StartActivityForResult failed: " + ex.Message);
                _filePathCallback = null;
                return false;
            }
        }

        public void HandleActivityResult(int requestCode, Android.App.Result resultCode, Android.Content.Intent? data)
        {
            if (requestCode != FileChooserRequestCode) return;

            System.Diagnostics.Debug.WriteLine($"[FileChooser] HandleActivityResult result={resultCode}, data={data?.Data}");

            Android.Net.Uri[] results;
            if (resultCode == Android.App.Result.Ok && data != null)
            {
                results = Android.Webkit.WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data);
                if (results == null || results.Length == 0)
                    results = new Android.Net.Uri[0];
            }
            else
            {
                results = new Android.Net.Uri[0];
            }

            try
            {
                _filePathCallback?.OnReceiveValue(results);
                System.Diagnostics.Debug.WriteLine($"[FileChooser] OnReceiveValue called, uris={results.Length}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[FileChooser] OnReceiveValue failed: " + ex.Message);
            }
            finally
            {
                _filePathCallback = null;
            }
        }
    }

    public class LauncherWebViewClient : Microsoft.Maui.Platform.MauiWebViewClient
    {
        public LauncherWebViewClient(Microsoft.Maui.Handlers.WebViewHandler handler) : base(handler) { }

        public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request)
        {
            var scheme = request?.Url?.Scheme;
            if (request != null && !request.IsForMainFrame
                && (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)))
                return false;

            return base.ShouldOverrideUrlLoading(view, request);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_isLaunching && _launchCts is { IsCancellationRequested: false } launchCts)
        {
            launchCts.Cancel();
            LoadingIndicator.IsVisible = false;
            return true;
        }

        if (string.IsNullOrEmpty(_currentAppName) && _launcherSettingsOpen)
        {
            _launcherSettingsOpen = false;
            _ = EvaluateLauncherScriptAsync("window.ucnlCloseSettings&&window.ucnlCloseSettings(true)");
            return true;
        }

        if (!string.IsNullOrEmpty(_currentAppName))
        {
            OnBackClicked(null, EventArgs.Empty);
            return true;
        }
        return base.OnBackButtonPressed();
    }

    private const string LauncherPageUrl = "file:///android_asset/launcher.html";

    private bool IsLauncherPageActive()
    {
        if (!string.IsNullOrEmpty(_currentAppName)) return false;
        var url = (MainWebView.Handler?.PlatformView as Android.Webkit.WebView)?.Url;
        return url != null && url.StartsWith(LauncherPageUrl, StringComparison.Ordinal);
    }

    private bool IsLocalPageActive()
    {
        try
        {
            var url = (MainWebView.Handler?.PlatformView as Android.Webkit.WebView)?.Url;
            if (string.IsNullOrEmpty(url)) return false;
            if (url.StartsWith("file:///android_asset/", StringComparison.Ordinal)) return true;
            if (!url.StartsWith("file://", StringComparison.Ordinal)) return false;

            var localPath = System.IO.Path.GetFullPath(new Uri(url).LocalPath);
            var cacheRoot = System.IO.Path.GetFullPath(_cacheService.CacheRoot).TrimEnd('/') + "/";
            return localPath.StartsWith(cacheRoot, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWebUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static void OpenInBrowser(string url)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenInBrowser] {ex.Message}");
            }
        });
    }

    private async void StartNativeGPS()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                return;
            }
        }

        StopNativeGPS();

        try
        {
            if (await StartAndroidGPS())
            {
                return;
            }
        }
        catch { }

        try
        {
            StartMauiGPS();
        }
        catch { }
    }

#endif

    private async Task<bool> StartAndroidGPS()
    {
        try
        {
            _locationManager = (Android.Locations.LocationManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.LocationService);

            if (_locationManager == null)
            {
                return false;
            }

            var gpsEnabled = _locationManager.IsProviderEnabled(Android.Locations.LocationManager.GpsProvider);
            var networkEnabled = _locationManager.IsProviderEnabled(Android.Locations.LocationManager.NetworkProvider);
            var passiveEnabled = _locationManager.IsProviderEnabled(Android.Locations.LocationManager.PassiveProvider);

            if (!gpsEnabled && !networkEnabled && !passiveEnabled)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await DisplayAlert(T("dialog.gps.title"), T("dialog.gps.message"), T("dialog.ok"));
                });
                return false;
            }

            _locationListener = new HarmonyLocationListener((location) =>
            {
                if (location != null && (location.Latitude != 0 || location.Longitude != 0))
                {
                    SendLocationToWebView(location.Latitude, location.Longitude,
                        location.Speed, location.Bearing);
                }
            });

            if (gpsEnabled)
            {
                _locationManager.RequestLocationUpdates(
                    Android.Locations.LocationManager.GpsProvider,
                    2000,
                    0,
                    _locationListener);
            }

            if (networkEnabled)
            {
                _locationManager.RequestLocationUpdates(
                    Android.Locations.LocationManager.NetworkProvider,
                    2000,
                    0,
                    _locationListener);
            }

            if (passiveEnabled)
            {
                _locationManager.RequestLocationUpdates(
                    Android.Locations.LocationManager.PassiveProvider,
                    2000,
                    0,
                    _locationListener);
            }

            var lastLocation = _locationManager.GetLastKnownLocation(Android.Locations.LocationManager.GpsProvider)
                ?? _locationManager.GetLastKnownLocation(Android.Locations.LocationManager.NetworkProvider)
                ?? _locationManager.GetLastKnownLocation(Android.Locations.LocationManager.PassiveProvider);

            if (lastLocation != null && (lastLocation.Latitude != 0 || lastLocation.Longitude != 0))
            {
                SendLocationToWebView(lastLocation.Latitude, lastLocation.Longitude,
                    lastLocation.Speed, lastLocation.Bearing);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void StartMauiGPS()
    {
        try
        {
            var cts = new CancellationTokenSource();
            _mauiGpsCts = cts;
            var token = cts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var request = new GeolocationRequest(
                            GeolocationAccuracy.Medium,
                            TimeSpan.FromSeconds(10));

                        var location = await Geolocation.Default.GetLocationAsync(request, token);

                        if (location != null && (location.Latitude != 0 || location.Longitude != 0))
                        {
                            SendLocationToWebView(location.Latitude, location.Longitude,
                                Convert.ToSingle(location.Speed ?? 0), Convert.ToSingle(location.Course ?? 0));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch { }

                    try
                    {
                        await Task.Delay(3000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }
        catch { }
    }

    private void SendLocationToWebView(double latitude, double longitude, float speed, float bearing)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await MainWebView.EvaluateJavaScriptAsync(
                    $"window._nativeGNSS = {{ " +
                    $"lat: {latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"lon: {longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"speed: {speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"course: {bearing.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}; " +
                    $"window.dispatchEvent(new Event('native-gnss-update'));");
            }
            catch { }
        });
    }

    private void StopNativeGPS()
    {
        try
        {
            if (_locationManager != null && _locationListener != null)
            {
                _locationManager.RemoveUpdates(_locationListener);
            }

            _mauiGpsCts?.Cancel();
            _mauiGpsCts?.Dispose();
            _mauiGpsCts = null;
        }
        catch { }
        finally
        {
            _locationListener = null;
            _locationManager = null;
        }
    }

    private void LoadLauncher()
    {
        StopCompass();
        _readLoopCts?.Cancel();
        _usbPollingStarted = false;
        _isLauncherLoaded = false;
        _isAppLoaded = false;
        _launcherSettingsOpen = false;
        _currentAppName = "";
        Toolbar.IsVisible = false;
        UpdateWebViewBaseColor();
        MainWebView.Source = "launcher.html";
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        _readLoopCts?.Cancel();
        _usbService.DiscardPendingWrites();
        StopNativeGPS();
        StopCompass();
        LoadLauncher();
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Url)) return;

        if (IsWebUrl(e.Url))
        {
            e.Cancel = true;
            OpenInBrowser(e.Url);
            return;
        }

        bool isBridgeCommand = e.Url.StartsWith("app://") || e.Url.StartsWith("uart://") || e.Url.StartsWith("file://save?");
        if (isBridgeCommand && !IsLocalPageActive())
        {
            e.Cancel = true;
            System.Diagnostics.Debug.WriteLine("[OnNavigating] Bridge command from non-local page rejected");
            return;
        }

        if (e.Url.StartsWith("app://savefile?"))
        {
            e.Cancel = true;
            HandleFileSave(Uri.UnescapeDataString(e.Url.Replace("app://savefile?", "")));
        }
        else if (TryParseLauncherCommand(e.Url, out var launcherCommand, out var launcherArgument))
        {
            e.Cancel = true;
            if (IsLauncherPageActive())
                HandleLauncherCommand(launcherCommand, launcherArgument);
            else
                System.Diagnostics.Debug.WriteLine($"[OnNavigating] Launcher command '{launcherCommand}' from non-launcher page ignored");
        }
        else if (e.Url.StartsWith("app://"))
        {
            e.Cancel = true;
            var appName = e.Url.Replace("app://", "");
            if (appName == "clear_cache")
            {
                if (IsLauncherPageActive() && !_isLaunching)
                    ClearAllCache();
            }
            else if (appName == "stop_gps")
                StopNativeGPS();
            else if (appName == "start_gps")
                StartNativeGPS();
            else if (appName == "start_compass")
                StartCompass();
            else if (appName == "stop_compass")
                StopCompass();
            else if (IsLauncherPageActive())
                LaunchApp(appName);
        }
        else if (e.Url.StartsWith("uart://write?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://write?", ""));
            var parts = raw.Split('|', 2);
            int portId = parts.Length > 1 && int.TryParse(parts[0], out var id) ? id : 0;
            var data = parts.Length > 1 ? parts[1] : raw;
            _usbService.Write(portId, data);
        }
        else if (e.Url.StartsWith("uart://setbaud?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://setbaud?", ""));
            var parts = raw.Split('|', 2);
            int portId = parts.Length > 0 && int.TryParse(parts[0], out var id) ? id : 0;
            int baudRate = parts.Length > 1 && int.TryParse(parts[1], out var br) ? br : 9600;

            _ = _usbService.SetBaudAsync(portId, baudRate);
        }
        else if (e.Url.StartsWith("file://save?"))
        {
            e.Cancel = true;
            HandleFileSave(Uri.UnescapeDataString(e.Url.Replace("file://save?", "")));
        }
        else if (e.Url.StartsWith("uart://close?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://close?", ""));
            int portId = int.TryParse(raw, out var id) ? id : 0;
            _ = _usbService.CloseAsync(portId);
        }
    }

    private async void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        LoadingIndicator.IsVisible = false;

        if (MainWebView.Handler?.PlatformView is Android.Webkit.WebView wv)
        {
            wv.AddJavascriptInterface(new NativeBridge(this), "NativeBridge");
        }

        if (!_isLauncherLoaded)
        {
            _isLauncherLoaded = true;
            Toolbar.IsVisible = false;
            PushLauncherConfig();
            await Task.Delay(500);
            await InjectLauncherScript();

            _ = SyncAllMissingAppsAsync();

            return;
        }

        if (!_isAppLoaded && !string.IsNullOrEmpty(_currentAppName))
        {
            _isAppLoaded = true;
            Toolbar.IsVisible = true;
            await InjectDeviceAdapter();
            StartCompass();

            if (_currentAppName == "uGNSSMonitor")
            {
                StartNativeGPS();
            }
        }
    }

    private void StartCompass()
    {
#if ANDROID
        if (_compassService != null)
        {
            return;
        }
        try
        {
            _compassService = new CompassService((heading) =>
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await MainWebView.EvaluateJavaScriptAsync(
                            $"window._nativeCompass = {{ heading: {heading.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}; " +
                            $"window.dispatchEvent(new Event('native-compass-update'));");
                    }
                    catch { }
                });
            });

            if (_compassService.IsAvailable)
            {
                _compassService.Start();
            }
        }
        catch { }
#endif
    }

    private void StopCompass()
    {
#if ANDROID
        try
        {
            _compassService?.Stop();
            _compassService = null;
        }
        catch { }
#endif
    }

    private async void ClearAllCache()
    {
        foreach (var key in _appUrls.Keys)
            Preferences.Remove($"{key}_lastSync");

        try
        {
            _cacheService.DeleteAll();
        }
        catch { }

#if ANDROID
        if (MainWebView.Handler?.PlatformView is Android.Webkit.WebView androidWebView)
        {
            androidWebView.ClearCache(true);
        }
#endif

        await DisplayAlert(T("dialog.cache.title"), T("dialog.cache.cleared"), T("dialog.ok"));
    }

    private async Task InjectLauncherScript()
    {
        try
        {
            string script = @"
        window.setAppDisabled = function(appName, disabled, hint) {
            var els = document.querySelectorAll('[data-app-name=""' + appName + '""]');
            els.forEach(function(el) {
                el.style.pointerEvents = disabled ? 'none' : '';
                el.style.opacity = disabled ? '0.5' : '';
                el.style.position = 'relative';
                var badge = el.querySelector('.app-download-badge');
                if (disabled) {
                    if (!badge) {
                        badge = document.createElement('div');
                        badge.className = 'app-download-badge';
                        badge.style.cssText = 'position:absolute; right:12px; top:50%; transform:translateY(-50%); font-size:11px; background:rgba(80,180,255,0.3); color:#fff; padding:4px 8px; border-radius:8px;';
                        el.appendChild(badge);
                    }
                    badge.textContent = hint || 'Загрузка...';
                } else if (badge) {
                    badge.remove();
                }
            });
        };
    ";
            await MainWebView.EvaluateJavaScriptAsync(script);
            bool hasDevice = _usbService.IsAnyPortOpen || await _usbService.TryConnectAsync(0, 9600);
            if (_isLaunching || !string.IsNullOrEmpty(_currentAppName)) return;
            await MainWebView.EvaluateJavaScriptAsync($"updateUsbStatus({hasDevice.ToString().ToLower()})");
            StartUsbWatcher(hasDevice);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InjectLauncherScript] {ex.Message}");
        }
    }

    private void StartUsbWatcher(bool initialState)
    {
        StopUsbWatcher();
        var cts = new CancellationTokenSource();
        _usbWatcherCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            bool lastState = initialState;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, token);

                    _usbService.CloseDetachedPorts();
                    bool connected = _usbService.IsAnyPortOpen
                        || await _usbService.TryConnectAsync(0, 9600);

                    if (token.IsCancellationRequested) break;
                    if (connected == lastState) continue;
                    lastState = connected;

                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            await MainWebView.EvaluateJavaScriptAsync($"updateUsbStatus({connected.ToString().ToLower()})");
                        }
                        catch { }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UsbWatcher] {ex.Message}");
                }
            }
        });
    }

    private void StopUsbWatcher()
    {
        var cts = _usbWatcherCts;
        _usbWatcherCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private async Task InjectDeviceAdapter()
    {
        try
        {
            string initStub = $@"
            if (window._initStub) {{
                window._initStub({{
                    appName: '{_currentAppName}',
                    preferredPort: 0
                }});
            }}
        ";
            await MainWebView.EvaluateJavaScriptAsync(initStub);

            using var stream = await FileSystem.OpenAppPackageFileAsync("device-adapter.js");
            using var reader = new StreamReader(stream);
            await MainWebView.EvaluateJavaScriptAsync(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InjectDeviceAdapter] {ex.Message}");
            return;
        }
        StartUsbPolling();
    }

    private void StartUsbPolling()
    {
        if (_usbPollingStarted) return;
        _usbPollingStarted = true;

        _readLoopCts?.Cancel();
        _readLoopCts = new CancellationTokenSource();
        var token = _readLoopCts.Token;

        Task.Run(() => PollPort(token, 0), token);

        if (_currentAppName == "AzimuthWebSuite" ||
            _currentAppName == "AzimuthLBLX" ||
            _currentAppName == "uWaveSuite")
            Task.Run(() => PollPort(token, 1), token);
    }

    private async Task PollPort(CancellationToken token, int portId)
    {
        var nextCheck = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextCheck)
                {
                    nextCheck = DateTime.UtcNow.AddSeconds(2);
                    _usbService.CloseDetachedPorts();
                    if (!_usbService.IsPortOpen(portId))
                        await _usbService.EnsureOpenAsync(portId);
                }

                if (!_usbService.IsPortOpen(portId))
                {
                    await Task.Delay(500, token);
                    continue;
                }

                var data = await _usbService.ReadAsync(portId, 100);
                if (data == null || data.Length == 0 || token.IsCancellationRequested)
                    continue;

                string script =
                    $"(function(){{var c=window._stubController{portId};if(!c)return;" +
                    $"var s=atob('{Convert.ToBase64String(data)}');var a=new Uint8Array(s.length);" +
                    "for(var i=0;i<s.length;i++)a[i]=s.charCodeAt(i);" +
                    "try{c.enqueue(a);}catch(e){}})()";

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        if (!token.IsCancellationRequested && MainWebView.IsLoaded && MainWebView.Handler?.PlatformView != null)
                            _ = MainWebView.EvaluateJavaScriptAsync(script);
                    }
                    catch { }
                });
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SyncAllMissingAppsAsync()
    {
        if (_isSyncingAll) return;
        _isSyncingAll = true;

        try
        {
            bool hasNetwork = false;
            try
            {
                hasNetwork = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
            }
            catch { }

            if (!hasNetwork)
            {
                System.Diagnostics.Debug.WriteLine("[SyncAll] Нет сети — пропускаем");
                return;
            }

            var missingApps = new List<string>();
            foreach (var kvp in _appUrls)
            {
                if (!_cacheService.HasLocalCopy(kvp.Key))
                    missingApps.Add(kvp.Key);
            }

            if (missingApps.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[SyncAll] Все приложения уже локально есть");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[SyncAll] Загружаем {missingApps.Count} приложений последовательно");

            await UpdateSyncStatus($"Загрузка приложений: 0/{missingApps.Count}...", true);

            int done = 0;
            foreach (var appName in missingApps)
            {
                await SetAppDisabledAsync(appName, true, "Загрузка...");
                _disabledApps.Add(appName);
                _syncingApps.Add(appName);

                try
                {
                    await UpdateSyncStatus($"Загрузка {appName}: {done + 1}/{missingApps.Count}...", true);

                    bool synced = await _cacheService.SyncAppAsync(
                        appName,
                        _appUrls[appName],
                        msg => System.Diagnostics.Debug.WriteLine(msg));

                    if (synced)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SyncAll] {appName} — OK");
                        Preferences.Set($"{appName}_lastSync", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[SyncAll] {appName} — не удалось");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SyncAll] {appName} — ошибка: {ex.Message}");
                }
                finally
                {
                    _syncingApps.Remove(appName);
                    _disabledApps.Remove(appName);

                    await SetAppDisabledAsync(appName, false, "");
                }

                done++;
            }

            await UpdateSyncStatus($"Загрузка завершена: {done}/{missingApps.Count}", false);
            System.Diagnostics.Debug.WriteLine($"[SyncAll] Завершено: {done}/{missingApps.Count}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncAll] Общая ошибка: {ex.Message}");
        }
        finally
        {
            _isSyncingAll = false;
        }
    }

    private async Task UpdateSyncStatus(string text, bool visible)
    {
        try
        {
            var escaped = text.Replace("\\", "\\\\").Replace("'", "\\'");
            await MainWebView.EvaluateJavaScriptAsync($@"
                (function() {{
                    var el = document.getElementById('syncStatus');
                    if (!el) return;
                    el.textContent = '{escaped}';
                    el.style.display = {(visible ? "'block'" : "'none'")};
                }})();
            ");
        }
        catch { }
    }

    private async Task SetAppDisabledAsync(string appName, bool disabled, string hint)
    {
        try
        {
            var escapedApp = appName.Replace("'", "\\'");
            var escapedHint = (hint ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
            await MainWebView.EvaluateJavaScriptAsync($@"
                (function() {{
                    if (typeof window.setAppDisabled === 'function') {{
                        window.setAppDisabled('{escapedApp}', {(disabled ? "true" : "false")}, '{escapedHint}');
                    }}
                }})();
            ");
        }
        catch { }
    }

    private async void LaunchApp(string appName)
    {
        if (_isLaunching || !string.IsNullOrEmpty(_currentAppName)) return;
        _isLaunching = true;
        var cts = new CancellationTokenSource();
        _launchCts = cts;
        bool navigated = false;
        try
        {
            navigated = await LaunchAppCoreAsync(appName, cts.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LaunchApp] {ex.Message}");
        }
        finally
        {
            _launchCts = null;
            cts.Dispose();
            _isLaunching = false;
            if (!navigated)
            {
                LoadingIndicator.IsVisible = false;
                if (_isLauncherLoaded)
                    _ = InjectLauncherScript();
            }
        }
    }

    private async Task<bool> FailLaunchAsync(CancellationToken token, string title, string message)
    {
        LoadingIndicator.IsVisible = false;
        if (!token.IsCancellationRequested)
            await DisplayAlert(title, message, T("dialog.ok"));
        return false;
    }

    private async Task<bool> LaunchAppCoreAsync(string appName, CancellationToken token)
    {
        if (!_appUrls.ContainsKey(appName))
            return await FailLaunchAsync(token, T("dialog.error.title"), T("dialog.app.notfound"));

        if (_syncingApps.Contains(appName))
            return await FailLaunchAsync(token, "Подождите", "Приложение ещё загружается, подождите окончания загрузки.");

        _isAppLoaded = false;
        LoadingIndicator.IsVisible = true;

        if (appName == "AzimuthWebSuite" || appName == "AzimuthLBLX")
        {
            try
            {
                var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
                if (status != PermissionStatus.Granted)
                {
                    status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                }
            }
            catch { }
            if (token.IsCancellationRequested) return false;

            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600, userInitiated: true))
                {
                    System.Diagnostics.Debug.WriteLine($"[LaunchApp] {appName}: AZM не найден, работаем без устройства");
                }
            }
            if (token.IsCancellationRequested) return false;
            _ = _usbService.TryConnectAsync(1, 9600, userInitiated: true);
        }
        else if (appName == "uGNSSMonitor")
        {
            _usbService.CloseAll();
            await Task.Delay(300);
            if (token.IsCancellationRequested) return false;

            if (!await _usbService.TryConnectAsync(0, userInitiated: true))
            {
                System.Diagnostics.Debug.WriteLine("[LaunchApp] uGNSSMonitor: USB не найден, работаем во встроенном режиме");
            }
        }
        else if (appName == "uWaveSuite")
        {
            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600, userInitiated: true))
                {
                    System.Diagnostics.Debug.WriteLine("[LaunchApp] uWaveSuite: uWave не найден, работаем без устройства");
                }
            }
            if (token.IsCancellationRequested) return false;
            _ = _usbService.TryConnectAsync(1, 9600, userInitiated: true);
        }
        else
        {
            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600, userInitiated: true))
                    return await FailLaunchAsync(token, T("dialog.usb.title"), T("dialog.usb.generic"));
            }
        }
        if (token.IsCancellationRequested) return false;

        string remoteUrl = _appUrls[appName];

        bool hasNetwork = false;
        try
        {
            hasNetwork = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
        }
        catch { }

        bool shouldSync = !_cacheService.HasLocalCopy(appName);

        if (hasNetwork && shouldSync)
        {
            _syncingApps.Add(appName);
            _disabledApps.Add(appName);
            await SetAppDisabledAsync(appName, true, "Загрузка...");

            try
            {
                bool synced = await _cacheService.SyncAppAsync(
                    appName,
                    remoteUrl,
                    msg => System.Diagnostics.Debug.WriteLine(msg));

                if (synced)
                    Preferences.Set($"{appName}_lastSync", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                if (token.IsCancellationRequested) return false;

                if (!synced && !_cacheService.HasLocalCopy(appName))
                    return await FailLaunchAsync(token, T("dialog.error.title"), T("dialog.app.loadfailed"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchApp] Sync failed: {ex.Message}");
                if (token.IsCancellationRequested) return false;

                if (!_cacheService.HasLocalCopy(appName))
                    return await FailLaunchAsync(token, T("dialog.error.title"), T("dialog.app.loadfailed"));
            }
            finally
            {
                _syncingApps.Remove(appName);
                _disabledApps.Remove(appName);
                await SetAppDisabledAsync(appName, false, "");
            }
        }
        else if (!hasNetwork && !_cacheService.HasLocalCopy(appName))
        {
            return await FailLaunchAsync(token, T("dialog.network.title"), T("dialog.network.message"));
        }

        if (token.IsCancellationRequested) return false;

        var localPath = Path.Combine(_cacheService.GetAppCachePath(appName), "index.html");
        var fileUrl = $"file://{localPath}";

        System.Diagnostics.Debug.WriteLine($"[LaunchApp] Opening: {fileUrl}");

        StopUsbWatcher();
        _currentAppName = appName;
        UpdateWebViewBaseColor();
        MainWebView.Source = fileUrl;
        return true;
    }

    private async void HandleFileSave(string payload)
    {
        try
        {
            var parts = payload.Split('|', 2);
            if (parts.Length < 2)
                throw new FormatException("Invalid save payload");

            var filename = SanitizeFileName(parts[0]);
            var bytes = Convert.FromBase64String(parts[1]);

            ShareFile(filename, bytes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HandleFileSave] {ex.Message}");
            try
            {
                await DisplayAlert(T("dialog.error.title"), T("dialog.file.savefailed"), T("dialog.ok"));
            }
            catch { }
        }
    }

    private static string SanitizeFileName(string filename)
    {
        var name = System.IO.Path.GetFileName(filename.Replace('\\', '/')) ?? "";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrEmpty(name) || name == "." || name == ".." ? "file" : name;
    }

    private void ShareFile(string filename, byte[] bytes)
    {
#if ANDROID
        var tempPath = System.IO.Path.Combine(FileSystem.CacheDirectory, filename);
        System.IO.File.WriteAllBytes(tempPath, bytes);

        var file = new Java.IO.File(tempPath);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
            Android.App.Application.Context,
            "com.unavlab.launcher.fileprovider",
            file);

        var intent = new Android.Content.Intent(Android.Content.Intent.ActionSend);
        intent.SetType("*/*");
        intent.PutExtra(Android.Content.Intent.ExtraStream, uri);
        intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);

        var chooser = Android.Content.Intent.CreateChooser(intent, T("native.save.title", filename)) ?? intent;
        chooser.AddFlags(Android.Content.ActivityFlags.NewTask);

        Android.App.Application.Context.StartActivity(chooser);
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopUsbWatcher();
        _readLoopCts?.Cancel();
        StopNativeGPS();
        StopCompass();
        _usbService.CloseAll();
    }

#if ANDROID

    internal void LogToPwa(string msg)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var escaped = msg.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n");
                await MainWebView.EvaluateJavaScriptAsync($"App.setStatus('{escaped}')");
            }
            catch { }
        });
    }

    internal async void PickLogFileViaFilePicker()
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Выберите лог",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "*/*" } }
                })
            });

            if (result == null)
            {
                SendLogErrorToWebView("Файл не выбран");
                return;
            }

            using var stream = await result.OpenReadAsync();
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            var text = await reader.ReadToEndAsync();

            const int chunkSize = 400_000;
            int totalChunks = (text.Length + chunkSize - 1) / chunkSize;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await MainWebView.EvaluateJavaScriptAsync("window._nativeLogContent = '';");

                    for (int i = 0; i < totalChunks; i++)
                    {
                        int start = i * chunkSize;
                        int len = Math.Min(chunkSize, text.Length - start);
                        var chunk = text.Substring(start, len);

                        var escaped = chunk
                            .Replace("\\", "\\\\")
                            .Replace("'", "\\'")
                            .Replace("\r", "\\r")
                            .Replace("\n", "\\n")
                            .Replace("\u2028", "\\u2028")
                            .Replace("\u2029", "\\u2029");

                        await MainWebView.EvaluateJavaScriptAsync(
                            $"window._nativeLogContent += '{escaped}';");
                        await Task.Delay(10);
                    }

                    await Task.Delay(1000);

                    await MainWebView.EvaluateJavaScriptAsync(
                        "if (window._nativeLogContent_ready) window._nativeLogContent_ready();");
                }
                catch (Exception ex)
                {
                    SendLogErrorToWebView("Ошибка передачи: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            SendLogErrorToWebView(ex.Message);
        }
    }

    private void SendLogErrorToWebView(string message)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var escaped = message.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n");
                await MainWebView.EvaluateJavaScriptAsync(
                    $"window._nativeLogError = '{escaped}'; " +
                    $"if (window._nativeLogContent_error) window._nativeLogContent_error();");
            }
            catch { }
        });
    }
#endif

}

#if ANDROID
public class HarmonyLocationListener : Java.Lang.Object, Android.Locations.ILocationListener
{
    private readonly Action<Android.Locations.Location?> _onLocation;

    public HarmonyLocationListener(Action<Android.Locations.Location?> onLocation)
    {
        _onLocation = onLocation;
    }

    public void OnLocationChanged(Android.Locations.Location? location)
    {
        _onLocation?.Invoke(location);
    }

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
    public void OnStatusChanged(string? provider, Android.Locations.Availability status, Android.OS.Bundle? extras) { }
}

public class NativeBridge : Java.Lang.Object
{
    private readonly MainPage _page;
    public NativeBridge(MainPage page) { _page = page; }

    [Java.Interop.Export("PickLogFile")]
    [Android.Webkit.JavascriptInterface]
    public void PickLogFile()
    {
        _page.PickLogFileViaFilePicker();
    }

}

#endif

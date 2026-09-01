using UCNLLauncher.Services;
using System.Net.Http;

namespace UCNLLauncher;

public partial class MainPage : ContentPage
{
    internal readonly UsbService _usbService;
    private CompassService? _compassService;
    private bool _isLauncherLoaded;
    private bool _isAppLoaded;
    private CancellationTokenSource? _readLoopCts;
    private readonly HttpClient _httpClient;
    private string _currentAppName = "";

    private Android.Locations.LocationManager? _locationManager;
    private HarmonyLocationListener? _locationListener;
    private CancellationTokenSource? _mauiGpsCts;

    private readonly Dictionary<string, string> _appUrls = new()
    {
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
        _ = RequestLocationPermissionsAsync();
        _usbService = new UsbService();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        MainWebView.Navigating += OnNavigating;
        MainWebView.Navigated += OnNavigated;

        ConfigureWebView();
        LoadLauncher();
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
        MainWebView.HandlerChanged += (s, e) =>
        {
            if (MainWebView.Handler?.PlatformView is Android.Webkit.WebView androidWebView)
            {
                androidWebView.Settings.CacheMode = Android.Webkit.CacheModes.CacheElseNetwork;
                androidWebView.Settings.JavaScriptEnabled = true;
                androidWebView.Settings.DomStorageEnabled = true;
                androidWebView.Settings.SetGeolocationEnabled(true);
                androidWebView.Settings.SetGeolocationDatabasePath(
                    Android.App.Application.Context.FilesDir?.Path ?? "/data/data/com.unavlab.ucnllauncher/files");
                androidWebView.SetWebChromeClient(new GeolocationWebChromeClient());
            }
        };
#endif
    }

#if ANDROID
    protected override void OnAppearing()
    {
        base.OnAppearing();
        DeviceDisplay.KeepScreenOn = true;
    }

    public class GeolocationWebChromeClient : Android.Webkit.WebChromeClient
    {
        public override void OnGeolocationPermissionsShowPrompt(string? origin, Android.Webkit.GeolocationPermissions.ICallback? callback)
        {
            callback?.Invoke(origin, true, false);
        }
    }
#endif

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

        // Пробуем Android LocationManager
        try
        {
            if (await StartAndroidGPS())
            {
                return;
            }
        }
        catch { }

        // Fallback на MAUI
        try
        {
            StartMauiGPS();
        }
        catch { }
    }

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
                    await DisplayAlert("GPS", "Пожалуйста, включите GPS для работы приложения", "OK");
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

            // Запрашиваем обновления от всех доступных провайдеров
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

            // Получаем последнюю известную локацию
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
            _mauiGpsCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (!_mauiGpsCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var request = new GeolocationRequest(
                            GeolocationAccuracy.Medium,
                            TimeSpan.FromSeconds(10));

                        var location = await Geolocation.Default.GetLocationAsync(request, _mauiGpsCts.Token);

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

                    await Task.Delay(3000, _mauiGpsCts.Token);
                }
            }, _mauiGpsCts.Token);
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
        _isLauncherLoaded = false;
        _isAppLoaded = false;
        _currentAppName = "";
        Toolbar.IsVisible = false;
        MainWebView.Source = "launcher.html";
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        _readLoopCts?.Cancel();
        StopNativeGPS();
        StopCompass();
        LoadLauncher();
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("app://savefile?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("app://savefile?", ""));
            HandleFileSave("file://save?" + raw);
        }
        else if (e.Url.StartsWith("app://"))
        {
            e.Cancel = true;
            var appName = e.Url.Replace("app://", "");
            if (appName == "clear_cache")
                ClearAllCache();
            else if (appName == "stop_gps")
                StopNativeGPS();
            else if (appName == "start_gps")
                StartNativeGPS();
            else if (appName == "start_compass")
                StartCompass();
            else if (appName == "stop_compass")
                StopCompass();
            else
                LaunchApp(appName);
        }
        else if (e.Url.StartsWith("uart://write?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://write?", ""));
            var parts = raw.Split('|', 2);
            int portId = parts.Length > 1 && int.TryParse(parts[0], out var id) ? id : 0;
            var data = parts.Length > 1 ? parts[1] : raw;
            _ = Task.Run(() => _usbService.WriteAsync(portId, data));
        }
        else if (e.Url.StartsWith("uart://setbaud?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://setbaud?", ""));
            var parts = raw.Split('|', 2);
            int portId = parts.Length > 0 && int.TryParse(parts[0], out var id) ? id : 0;
            int baudRate = parts.Length > 1 && int.TryParse(parts[1], out var br) ? br : 9600;

            _ = Task.Run(async () =>
            {
                _usbService.ClosePort(portId);
                await Task.Delay(300);
                await _usbService.TryConnectAsync(portId, baudRate);
            });
        }
        else if (e.Url.StartsWith("file://save?"))
        {
            e.Cancel = true;
            HandleFileSave(e.Url);
        }
        else if (e.Url.StartsWith("uart://close?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://close?", ""));
            int portId = int.TryParse(raw, out var id) ? id : 0;
            _usbService.ClosePort(portId);
        }
    }

    private async void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        LoadingIndicator.IsVisible = false;

        if (!_isLauncherLoaded)
        {
            _isLauncherLoaded = true;
            Toolbar.IsVisible = false;
            await Task.Delay(500);
            await InjectLauncherScript();
        }
        else if (!_isAppLoaded && e.Url.Contains("native=1"))
        {
            _isAppLoaded = true;
            Toolbar.IsVisible = true;
            await InjectDeviceAdapter();

            // Запускаем компас автоматически
            StartCompass();

            foreach (var kvp in _appUrls)
            {
                if (e.Url.Contains(kvp.Value))
                {
                    Preferences.Set(kvp.Key + "_cache", e.Url);
                    break;
                }
            }
        }
    }

    private void StartCompass()
    {
#if ANDROID
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
            Preferences.Remove(key + "_cache");

#if ANDROID
        if (MainWebView.Handler?.PlatformView is Android.Webkit.WebView androidWebView)
        {
            androidWebView.ClearCache(true);
        }
#endif

        await DisplayAlert("Кэш", "Кэш всех приложений очищен.", "OK");
    }

    private async Task InjectLauncherScript()
    {
        string script = @"
        window.launchApp = function(appName) { window.location.href = 'app://' + appName; };
        window.updateUsbStatus = function(connected) {
            var dot = document.getElementById('statusDot');
            var text = document.getElementById('statusText');
            if (dot && text) {
                dot.className = connected ? 'status-dot connected' : 'status-dot';
                text.textContent = connected ? 'USB подключено' : 'USB не найдено';
            }
        };
    ";
        await MainWebView.EvaluateJavaScriptAsync(script);
        bool hasDevice = await _usbService.TryConnectAsync(0, 9600);
        await MainWebView.EvaluateJavaScriptAsync($"updateUsbStatus({hasDevice.ToString().ToLower()})");
        StartUsbWatcher();
    }

    private void StartUsbWatcher()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(3000);

                    bool wasConnected = _usbService.IsAnyPortOpen;
                    bool nowConnected = _usbService.IsAnyPortOpen;

                    if (!nowConnected && wasConnected)
                    {
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            await MainWebView.EvaluateJavaScriptAsync("updateUsbStatus(false)");
                        });
                    }
                    else if (!nowConnected)
                    {
                        bool connected = await _usbService.TryConnectAsync(0, 9600);
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            await MainWebView.EvaluateJavaScriptAsync($"updateUsbStatus({connected.ToString().ToLower()})");
                        });
                    }
                }
                catch { break; }
            }
        });
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
        catch
        {
            return;
        }
        StartUsbPolling();
    }

    private void StartUsbPolling()
    {
        _readLoopCts?.Cancel();
        _readLoopCts = new CancellationTokenSource();
        var token = _readLoopCts.Token;

        Task.Run(() => PollPort(token, 0), token);

        if (_currentAppName == "AzimuthWebSuite" || _currentAppName == "AzimuthLBLX")
            Task.Run(() => PollPort(token, 1), token);
    }

    private async Task PollPort(CancellationToken token, int portId)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_usbService.IsPortOpen(portId))
                {
                    await Task.Delay(500, token);
                    continue;
                }

                string? data = null;
                try
                {
                    data = await _usbService.ReadAsync(portId, 100);
                }
                catch
                {
                    await Task.Delay(500, token);
                    continue;
                }

                if (!string.IsNullOrEmpty(data))
                {
                    string escaped = data.Replace("\\", "\\\\").Replace("'", "\\'")
                        .Replace("\n", "\\n").Replace("\r", "");

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            if (MainWebView.IsLoaded && MainWebView.Handler?.PlatformView != null)
                            {
                                MainWebView.EvaluateJavaScriptAsync(
                                    $"if(window._stubController{portId}) window._stubController{portId}.enqueue(new TextEncoder().encode('{escaped}'))");
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (TaskCanceledException) { break; }
            catch
            {
                await Task.Delay(1000, token);
            }
        }
    }

    private async void LaunchApp(string appName)
    {
        if (!_appUrls.ContainsKey(appName))
        {
            await DisplayAlert("Ошибка", "Приложение не найдено", "OK");
            return;
        }

        _currentAppName = appName;
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

            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600))
                {
                    LoadingIndicator.IsVisible = false;
                    await DisplayAlert("USB", "Устройство AZM не найдено", "OK");
                    return;
                }
            }
            _ = _usbService.TryConnectAsync(1, 0);
        }
        else if (appName == "uGNSSMonitor")
        {
            _usbService.CloseAll();
            await Task.Delay(300);

            if (!await _usbService.TryConnectAsync(0))
            {
                LoadingIndicator.IsVisible = false;
                await DisplayAlert("USB", "GNSS не найден", "OK");
                return;
            }
        }
        else
        {
            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600))
                {
                    LoadingIndicator.IsVisible = false;
                    await DisplayAlert("USB", "Устройство не найдено", "OK");
                    return;
                }
            }
        }

        string appUrl = _appUrls[appName];
        string fullUrl = appUrl.Contains("?")
            ? $"{appUrl}&native=1"
            : $"{appUrl}?native=1";

        _isAppLoaded = false;

        try
        {
            var response = await _httpClient.GetAsync(appUrl);
            if (response.IsSuccessStatusCode)
            {
                MainWebView.Source = fullUrl;
                return;
            }
        }
        catch { }

        string? cachedUrl = Preferences.Get(appName + "_cache", null);
        if (cachedUrl != null)
        {
            MainWebView.Source = cachedUrl;
        }
        else
        {
            LoadingIndicator.IsVisible = false;
            await DisplayAlert("Нет сети", "Приложение недоступно офлайн", "OK");
        }
    }

    private void HandleFileSave(string url)
    {
        try
        {
            var raw = Uri.UnescapeDataString(url.Replace("file://save?", ""));
            var parts = raw.Split('|', 2);

            if (parts.Length < 2) return;

            var filename = parts[0];
            var base64Content = parts[1];
            var bytes = Convert.FromBase64String(base64Content);

            ShareFile(filename, bytes);
        }
        catch { }
    }

    private void ShareFile(string filename, byte[] bytes)
    {
#if ANDROID
        try
        {
            var tempPath = System.IO.Path.Combine(FileSystem.CacheDirectory, filename);
            System.IO.File.WriteAllBytes(tempPath, bytes);

            var file = new Java.IO.File(tempPath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                Android.App.Application.Context,
                "com.unavlab.ucnllauncher.fileprovider",
                file);

            var intent = new Android.Content.Intent(Android.Content.Intent.ActionSend);
            intent.SetType("*/*");
            intent.PutExtra(Android.Content.Intent.ExtraStream, uri);
            intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);

            var chooser = Android.Content.Intent.CreateChooser(intent, $"Сохранить {filename}");
            chooser.AddFlags(Android.Content.ActivityFlags.NewTask);

            Android.App.Application.Context.StartActivity(chooser);
        }
        catch { }
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _readLoopCts?.Cancel();
        StopNativeGPS();
        StopCompass();
        _usbService.CloseAll();
    }
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
#endif
using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Hardware.Usb;
using UCNLLauncher.Services;

namespace UCNLLauncher
{
    [IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached })]
    [MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.Locale | ConfigChanges.LayoutDirection)]
    public class MainActivity : MauiAppCompatActivity
    {
        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            LocalizationService.Current.Refresh();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode == MainPage.GeolocationWebChromeClient.FileChooserRequestCode)
                MainPage.GeolocationWebChromeClient.Active?.HandleActivityResult(requestCode, resultCode, data);
        }
    }
}

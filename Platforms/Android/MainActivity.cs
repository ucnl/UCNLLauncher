using Android.App;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;

namespace UCNLLauncher
{
    [IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached })]
    [MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        public static MainActivity? Instance { get; private set; }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Instance = this;
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (Microsoft.Maui.Controls.Application.Current?.MainPage is UCNLLauncher.MainPage page)
            {
                page.HandleFileChooserResult(requestCode, resultCode, data);
            }
        }
    }
}

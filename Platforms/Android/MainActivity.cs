using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace UCNLLauncher
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var permissions = new[]
            {
                Manifest.Permission.AccessFineLocation,
                Manifest.Permission.AccessCoarseLocation,
                Manifest.Permission.AccessBackgroundLocation
            };

            var missing = permissions.Where(p =>
                ContextCompat.CheckSelfPermission(this, p) != Permission.Granted).ToArray();

            if (missing.Length > 0)
            {
                ActivityCompat.RequestPermissions(this, missing, 1);
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (requestCode == 1)
            {
                for (int i = 0; i < permissions.Length; i++)
                {
                    System.Diagnostics.Debug.WriteLine($"[Permission] {permissions[i]}: {(grantResults[i] == Permission.Granted ? "Granted" : "Denied")}");
                }
            }
        }
    }
}

using UCNLLauncher.Services;

namespace UCNLLauncher
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            ThemeService.Current.Initialize(this);

            MainPage = new AppShell();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = base.CreateWindow(activationState);
            window.Activated += OnWindowActivated;
            return window;
        }

        private static void OnWindowActivated(object? sender, EventArgs e) => ThemeService.Current.Refresh();
    }
}

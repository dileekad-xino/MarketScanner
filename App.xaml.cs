using MarketScanner.Views;
using MarketScanner.ViewModels;
using MarketScanner.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace MarketScanner
{
    public partial class App : Application
    {
        private readonly ScannerViewModel _scannerViewModel;
        private readonly IAppShutdownHandler _shutdownHandler;
        private bool _isShuttingDown = false;
        private Window? _mainWindow;

        public App(IServiceProvider services)
        {
            InitializeComponent();

            // Resolve scanner page from DI container
            _scannerViewModel = services.GetRequiredService<ScannerViewModel>();
            _shutdownHandler = services.GetRequiredService<IAppShutdownHandler>();
            var layout = services.GetRequiredService<ColumnLayoutService>();
            var scannerPage = new ScannerPage(_scannerViewModel, layout);

            MainPage = new AppShell(scannerPage);

            // Start candlestick builder
            try
            {
                var candlestickBuilder = services.GetService<ICandlestickBuilder>();
                if (candlestickBuilder != null)
                {
                    candlestickBuilder.Start();
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash the app
                System.Diagnostics.Debug.WriteLine($"Failed to start candlestick builder: {ex.Message}");
            }
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            _mainWindow = base.CreateWindow(activationState);
            _mainWindow.Destroying += OnMainWindowDestroying;
            return _mainWindow;
        }

        private async void OnMainWindowDestroying(object? sender, EventArgs e)
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            var closingWindow = sender as Window;
            if (closingWindow != null)
            {
                // Close all other windows (e.g. Daily P/L) so the app exits cleanly
                var windows = Application.Current?.Windows?.ToList() ?? new List<Window>();
                foreach (var window in windows)
                {
                    if (window != closingWindow)
                    {
                        try
                        {
                            Application.Current?.CloseWindow(window);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error closing window: {ex.Message}");
                        }
                    }
                }
            }

            try
            {
                await _shutdownHandler.HandleShutdownQuietAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Shutdown handler error: {ex.Message}");
            }
            finally
            {
                Shutdown();
                Application.Current?.Quit();
            }
        }

        protected override void OnSleep()
        {
            // Do not run shutdown on Sleep (minimize/deactivate). Scanner and updates keep running.
            base.OnSleep();
        }

        private void Shutdown()
        {
            _isShuttingDown = true;
            try
            {
                _scannerViewModel.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Scanner shutdown error: {ex.Message}");
            }
        }
    }
}

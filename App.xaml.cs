using MarketScanner.Views;
using MarketScanner.ViewModels;
using MarketScanner.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MarketScanner
{
    public partial class App : Application
    {
        private readonly ScannerViewModel _scannerViewModel;
        private readonly IAppShutdownHandler _shutdownHandler;
        private bool _isShuttingDown = false;

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

        protected override async void OnSleep()
        {
            // Check if we're already shutting down to avoid recursive calls
            if (_isShuttingDown)
            {
                base.OnSleep();
                Shutdown();
                return;
            }

            // Handle shutdown with confirmation and position closure
            var shouldShutdown = await _shutdownHandler.HandleShutdownAsync();
            
            if (!shouldShutdown)
            {
                // User cancelled shutdown - prevent app from closing
                return;
            }

            // User confirmed or no positions to close - proceed with shutdown
            _isShuttingDown = true;
            base.OnSleep();
            Shutdown();
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

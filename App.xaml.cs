using MarketScanner.Views;
using MarketScanner.ViewModels;
using MarketScanner.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MarketScanner
{
    public partial class App : Application
    {
        private readonly ScannerViewModel _scannerViewModel;

        public App(IServiceProvider services)
        {
            InitializeComponent();

            // Resolve scanner page from DI container
            _scannerViewModel = services.GetRequiredService<ScannerViewModel>();
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

        protected override void OnSleep()
        {
            base.OnSleep();
            Shutdown();
        }

        private void Shutdown()
        {
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

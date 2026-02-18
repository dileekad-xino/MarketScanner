using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using MarketScanner.Services.Impl;
using MarketScanner.ViewModels;
using MarketScanner.Views;
using MarketScanner.Config;
using MarketScanner.Database;
using MarketScanner.Models;
using System.Reflection;
using CommunityToolkit.Maui;
using NReco.Logging.File;
using System.IO;

namespace MarketScanner
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Configuration
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("MarketScanner.appsettings.json");
            if (stream != null)
            {
                builder.Configuration.AddJsonStream(stream);
            }

            builder.Configuration
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            // Database Services
            builder.Services.AddSingleton<DatabaseInitializer>();
            builder.Services.AddSingleton<IDatabaseContext, DatabaseContext>();

            // Services
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<IRsiSettingsService, RsiSettingsService>();
            builder.Services.AddSingleton<ICciSettingsService, CciSettingsService>();
            builder.Services.AddSingleton<ColumnLayoutService>();

            // Position closure and confirmation dialog services
            builder.Services.AddSingleton<IConfirmationDialogService, Services.Impl.ConfirmationDialogService>();
            builder.Services.AddSingleton<IPositionClosureService, Services.Impl.PositionClosureService>();
            builder.Services.AddSingleton<IAppShutdownHandler, Services.Impl.AppShutdownHandler>();

            // AlgoRunnerManagerService (depends on confirmation and position closure services)
            builder.Services.AddSingleton<Services.AlgoRunnerManagerService>();

            // Register database tables after DatabaseInitializer is registered
            builder.Services.AddSingleton<IWatchlistService>(sp =>
            {
                var initializer = sp.GetRequiredService<DatabaseInitializer>();
                // Register tables for watchlists database
                initializer.RegisterTable<Watchlist>("watchlists.db3");
                initializer.RegisterTable<WatchlistItem>("watchlists.db3");

                return new WatchlistService(
                    sp.GetRequiredService<ILogger<WatchlistService>>(),
                    sp.GetRequiredService<IDatabaseContext>());
            });

            // Register TradeService
            builder.Services.AddSingleton<ITradeService>(sp =>
            {
                var initializer = sp.GetRequiredService<DatabaseInitializer>();
                // Register Trade table for trades database
                initializer.RegisterTable<Trade>("trades.db3");

                return new TradeService(
                    sp.GetRequiredService<ILogger<TradeService>>(),
                    sp.GetRequiredService<IDatabaseContext>());
            });

            // Register IBKR configuration
            builder.Services.Configure<IbkrConfig>(builder.Configuration.GetSection("Ibkr"));
            builder.Services.AddSingleton<IbkrConfig>(provider =>
                provider.GetRequiredService<IOptions<IbkrConfig>>().Value);

            // Register Candlestick configuration
            builder.Services.Configure<CandlestickConfig>(builder.Configuration.GetSection("Candlestick"));
            builder.Services.AddSingleton<CandlestickConfig>(provider =>
                provider.GetRequiredService<IOptions<CandlestickConfig>>().Value);

            builder.Services.AddSingleton<AppSettings>(provider =>
            {
                var config = provider.GetRequiredService<IConfiguration>();
                return new AppSettings
                {
                    IbkrProxyBaseUrl = config["IbkrProxyBaseUrl"] ?? "",
                    ApiKey = config["ApiKey"] ?? "",
                    RefreshIntervalSeconds = int.Parse(config["RefreshIntervalSeconds"] ?? "30"),
                    DebounceMilliseconds = int.Parse(config["DebounceMilliseconds"] ?? "500"),
                    EnableVerboseLogging = bool.Parse(config["EnableVerboseLogging"] ?? "false"),
                    EnableFundamentals = bool.Parse(config["Features:EnableFundamentals"] ?? "true"),
                    AutoStartOnLaunch = bool.Parse(config["AutoStartOnLaunch"] ?? "false")
                };
            });

            // Dispatcher service
            builder.Services.AddSingleton<IDispatcherService, MauiDispatcherService>();

            // IBKR Services - Single unified gateway service
            builder.Services.AddSingleton<IbkrGatewayService>();
            builder.Services.AddSingleton<IScanner>(sp => sp.GetRequiredService<IbkrGatewayService>());
            builder.Services.AddSingleton<IMarketDataService>(sp => sp.GetRequiredService<IbkrGatewayService>());
            builder.Services.AddSingleton<IInstrumentMetadataProvider, IbkrInstrumentMetadataProvider>();

            // Symbol Search Service
            builder.Services.AddSingleton<ISymbolSearchService>(sp =>
            {
                var ibkrService = sp.GetService<IbkrGatewayService>();
                var logger = sp.GetRequiredService<ILogger<SymbolSearchService>>();
                return new SymbolSearchService(ibkrService, logger);
            });

            // Fallback playback services (for offline testing)
            builder.Services.AddSingleton<PlaybackFallback>();

            // MAUI Services
            builder.Services.AddSingleton<IConnectivity>(provider =>
                Microsoft.Maui.Networking.Connectivity.Current);

            // Candlestick Services
            builder.Services.AddSingleton<ICandlestickStorage, CandlestickStorage>();
            builder.Services.AddSingleton<ICandlestickBuilder, CandlestickBuilder>();

            // Algorithm Services
            // Register individual strategies
            builder.Services.AddSingleton<MarketScanner.Services.Impl.RSIAlgoStrategy>(sp =>
            {
                return new MarketScanner.Services.Impl.RSIAlgoStrategy(
                    sp.GetRequiredService<ICandlestickStorage>(),
                    sp.GetRequiredService<CandlestickConfig>(),
                    sp.GetRequiredService<IRsiSettingsService>(),
                    sp.GetRequiredService<MarketScanner.Services.Impl.RsiEngine>(),
                    sp.GetRequiredService<ILogger<MarketScanner.Services.Impl.RSIAlgoStrategy>>(),
                    sp.GetService<ITradeService>()); // Optional dependency
            });
            builder.Services.AddSingleton<MarketScanner.Services.Impl.CciAlgoStrategy>(sp =>
            {
                return new MarketScanner.Services.Impl.CciAlgoStrategy(
                sp.GetRequiredService<ICandlestickStorage>(),
                sp.GetRequiredService<CandlestickConfig>(),
                sp.GetRequiredService<ICciSettingsService>(),
                sp.GetRequiredService<MarketScanner.Services.Impl.CciEngine>(),
                sp.GetRequiredService<ILogger<MarketScanner.Services.Impl.CciAlgoStrategy>>()
                );
            });
            builder.Services.AddSingleton<MarketScanner.Services.Impl.MacdEngine>();
            builder.Services.AddSingleton<MarketScanner.Services.Impl.MacdStrategy>();
            builder.Services.AddSingleton<MarketScanner.Services.Impl.RsiEngine>();
            builder.Services.AddSingleton<MarketScanner.Services.Impl.CciEngine>();
            builder.Services.AddSingleton<MarketScanner.Services.Impl.EmaEngine>();
            builder.Services.AddSingleton<MarketScanner.Services.Impl.Ema20AlgoStrategy>();

            // Register composite strategy that combines RSI, MACD, CCI, and EMA 20
            builder.Services.AddSingleton<MarketScanner.Services.IAlgoStrategy>(sp =>
            {
                var rsiStrategy = sp.GetRequiredService<MarketScanner.Services.Impl.RSIAlgoStrategy>();
                var macdStrategy = sp.GetRequiredService<MarketScanner.Services.Impl.MacdStrategy>();
                var cciStrategy = sp.GetRequiredService<MarketScanner.Services.Impl.CciAlgoStrategy>();
                var ema20Strategy = sp.GetRequiredService<MarketScanner.Services.Impl.Ema20AlgoStrategy>();
                var logger = sp.GetRequiredService<ILogger<MarketScanner.Services.Impl.AlgoStrategy>>();

                return new MarketScanner.Services.Impl.AlgoStrategy(
                    new MarketScanner.Services.IAlgoStrategy[] { rsiStrategy, macdStrategy, cciStrategy, ema20Strategy },
                    sp.GetRequiredService<ICciSettingsService>(),
                    logger);
            });

            // ViewModels
            builder.Services.AddTransient<ScannerViewModel>(sp =>
            {
                return new ScannerViewModel(
                    sp.GetRequiredService<IScanner>(),
                    sp.GetRequiredService<IDispatcherService>(),
                    sp.GetRequiredService<ILogger<ScannerViewModel>>(),
                    sp.GetRequiredService<IWatchlistService>(),
                    sp.GetRequiredService<ITradeService>(),
                    sp.GetRequiredService<IRsiSettingsService>(),
                    sp.GetRequiredService<IAlgoStrategy>(),
                    sp.GetRequiredService<Services.AlgoRunnerManagerService>(),
                    sp.GetRequiredService<IConfirmationDialogService>(),
                    sp.GetRequiredService<IPositionClosureService>());
            });
            // WatchlistViewModel is created on-demand by ScannerViewModel

            // Views
            builder.Services.AddTransient<ScannerPage>();

            // Logging
            var logsDir = ResolveLogsDir();
            Console.WriteLine($"[Logging] Writing to: {logsDir}");

            builder.Logging
                .AddConsole()
                .AddDebug()
                .AddFile(Path.Combine(logsDir, "app-{Date}.log"), opts =>
                {
#if DEBUG
                    opts.MinLevel = LogLevel.Trace;
#else
                    opts.MinLevel = LogLevel.Information;
#endif
                    opts.MaxRollingFiles = 7;
                    opts.FileSizeLimitBytes = 10_000_000;
                    opts.Append = true;
                });

            return builder.Build();
        }

        private static string ResolveLogsDir()
        {
            // Try to use a logs directory in the project root
            var currentDir = Directory.GetCurrentDirectory();
            var projectDir = currentDir;

            // Walk up to find the project directory
            while (!string.IsNullOrEmpty(projectDir) && !File.Exists(Path.Combine(projectDir, "MarketScanner.csproj")))
            {
                projectDir = Path.GetDirectoryName(projectDir);
            }

            if (!string.IsNullOrEmpty(projectDir))
            {
                var logsDir = Path.Combine(projectDir, "Logs");
                try
                {
                    Directory.CreateDirectory(logsDir);
                    return logsDir;
                }
                catch
                {
                    // Fall through to default
                }
            }

            // Fallback to AppData
            var appData = Path.Combine(FileSystem.AppDataDirectory, "Logs");
            Directory.CreateDirectory(appData);
            return appData;
        }
    }
}

using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
#if WINDOWS
using Microsoft.UI.Xaml.Input;
using Windows.System;
#endif

namespace MarketScanner.Views;

public partial class QuoteContentView : ContentView
{
    private ViewModels.QuoteViewModel? _viewModel;
    private bool _chartReady;
    private Task? _loadHtmlTask;
    private Models.ChartSnapshot? _pendingSnapshot; // Store snapshot until chart is ready
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public QuoteContentView()
    {
        System.Diagnostics.Debug.WriteLine("═══════════════════════════════════════════════════");
        System.Diagnostics.Debug.WriteLine("QuoteContentView: Constructor called");
        System.Diagnostics.Debug.WriteLine("═══════════════════════════════════════════════════");
        InitializeComponent();
        System.Diagnostics.Debug.WriteLine("QuoteContentView: InitializeComponent completed");
        System.Diagnostics.Debug.WriteLine("QuoteContentView: ChartWebView.Navigated handler is defined in XAML ✓");
        
        // Track when view becomes visible
        PropertyChanged += OnPropertyChanged;
        
        Loaded += OnLoaded;
        
        // Try to load HTML immediately
        System.Diagnostics.Debug.WriteLine("QuoteContentView: Starting immediate chart HTML load attempt");
        _ = EnsureChartHtmlAsync();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsVisible))
        {
            System.Diagnostics.Debug.WriteLine($"QuoteContentView: IsVisible changed to {IsVisible}");
            if (IsVisible)
            {
                System.Diagnostics.Debug.WriteLine("QuoteContentView: View became VISIBLE, triggering chart load");
                _ = EnsureChartHtmlAsync();
            }
        }
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("QuoteContentView: Loaded event fired");
        Loaded -= OnLoaded;
        _ = EnsureChartHtmlAsync();
    }

    protected override void OnBindingContextChanged()
    {
        System.Diagnostics.Debug.WriteLine($"QuoteContentView: BindingContext changing from {_viewModel?.GetType().Name} to {(BindingContext as ViewModels.QuoteViewModel)?.GetType().Name}");
        System.Diagnostics.Debug.WriteLine($"QuoteContentView: IsVisible={IsVisible}, Parent={Parent?.GetType().Name}");
        
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnBindingContextChanged();

        _viewModel = BindingContext as ViewModels.QuoteViewModel;
        if (_viewModel != null)
        {
            System.Diagnostics.Debug.WriteLine("QuoteContentView: QuoteViewModel bound, hooking PropertyChanged");
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            
            // Trigger chart load when binding context is set and view is visible
            if (IsVisible)
            {
                System.Diagnostics.Debug.WriteLine("QuoteContentView: View is visible AND ViewModel is bound, ensuring chart HTML is loaded");
                _ = EnsureChartHtmlAsync();
            }
            
            _ = TryPushChartSnapshotAsync();
        }
    }

#if WINDOWS
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        
        System.Diagnostics.Debug.WriteLine($"QuoteContentView: OnHandlerChanged called, Handler is null: {Handler == null}");
        
        // Hook up keyboard events for Windows
        if (SymbolEntry?.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
        {
            textBox.KeyDown += OnSymbolEntryKeyDown;
        }
        
        // Check if WebView handler is created and try to load HTML
        if (ChartWebView?.Handler != null)
        {
            System.Diagnostics.Debug.WriteLine("QuoteContentView: ChartWebView handler is available, attempting to load chart HTML");
            _ = EnsureChartHtmlAsync();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("QuoteContentView: WARNING - ChartWebView handler is NULL, will retry on Loaded event");
        }
    }

    private void OnSymbolEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            if (!viewModel.ShowSearchResults || viewModel.SearchResults.Count == 0)
                return;

            switch (e.Key)
            {
                case VirtualKey.Up:
                    e.Handled = true;
                    viewModel.NavigateSearchResultsUpCommand.Execute(null);
                    break;
                case VirtualKey.Down:
                    e.Handled = true;
                    viewModel.NavigateSearchResultsDownCommand.Execute(null);
                    break;
                case VirtualKey.Enter:
                    e.Handled = true;
                    if (viewModel.SelectedSearchResultIndex >= 0 && viewModel.SelectedSearchResultIndex < viewModel.SearchResults.Count)
                    {
                        var selectedResult = viewModel.SearchResults[viewModel.SelectedSearchResultIndex];
                        viewModel.SelectSearchResultCommand.Execute(selectedResult);
                        // Trigger AddQuoteCommand to add the selected symbol
                        viewModel.AddQuoteCommand.Execute(null);
                    }
                    else
                    {
                        // Fall back to normal Enter behavior (AddQuoteCommand)
                        viewModel.AddQuoteCommand.Execute(null);
                    }
                    break;
            }
        }
    }
#endif

    private void OnSymbolTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Update ViewModel property to trigger OnNewSymbolTextChanged
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            viewModel.NewSymbolText = e.NewTextValue ?? "";
        }
    }

    private void OnSymbolEntryUnfocused(object? sender, FocusEventArgs e)
    {
        // Hide search results when entry loses focus
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            viewModel.ShowSearchResults = false;
        }
    }

    private async Task EnsureChartHtmlAsync()
    {
        System.Diagnostics.Debug.WriteLine($"QuoteContentView: EnsureChartHtmlAsync called, _loadHtmlTask is null: {_loadHtmlTask == null}");
        System.Diagnostics.Debug.WriteLine($"QuoteContentView: ChartWebView is null: {ChartWebView == null}");
        
        if (ChartWebView == null)
        {
            System.Diagnostics.Debug.WriteLine("✗ QuoteContentView: ERROR - ChartWebView is NULL! Cannot load HTML.");
            System.Diagnostics.Debug.WriteLine("✗ This means InitializeComponent() didn't create the WebView properly");
            return;
        }
        
        if (_loadHtmlTask != null)
        {
            System.Diagnostics.Debug.WriteLine("QuoteContentView: HTML already loading/loaded, awaiting existing task");
            await _loadHtmlTask;
            return;
        }

        System.Diagnostics.Debug.WriteLine("QuoteContentView: Starting new LoadChartHtmlAsync task");
        _loadHtmlTask = LoadChartHtmlAsync();
        
        try
        {
            await _loadHtmlTask;
            System.Diagnostics.Debug.WriteLine("✓ QuoteContentView: LoadChartHtmlAsync completed successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ QuoteContentView: LoadChartHtmlAsync FAILED with exception: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"✗ Message: {ex.Message}");
            throw;
        }
    }

    // Store the JS library for injection after page load (Microsoft Learn recommended approach)
    private string? _lightweightChartsLibrary;
    
    private async Task LoadChartHtmlAsync()
    {
        System.Diagnostics.Debug.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        System.Diagnostics.Debug.WriteLine("║ LoadChartHtmlAsync: STARTING (EvaluateJavaScriptAsync approach)     ║");
        System.Diagnostics.Debug.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        
        try
        {
            System.Diagnostics.Debug.WriteLine("LoadChartHtmlAsync: Step 1 - Loading quotes_chart.html");
            
            // Load the HTML file
            using var htmlStream = await FileSystem.OpenAppPackageFileAsync("quotes_chart.html").ConfigureAwait(false);
            using var htmlReader = new StreamReader(htmlStream);
            var html = await htmlReader.ReadToEndAsync().ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"✓ LoadChartHtmlAsync: HTML loaded successfully! Length={html.Length} bytes");

            // Load the JavaScript library into memory (we'll inject it via EvaluateJavaScriptAsync)
            try
            {
                System.Diagnostics.Debug.WriteLine("LoadChartHtmlAsync: Loading lightweight-charts.standalone.production.js for later injection");
                using var jsStream = await FileSystem.OpenAppPackageFileAsync("lightweight-charts.standalone.production.js").ConfigureAwait(false);
                using var jsReader = new StreamReader(jsStream);
                _lightweightChartsLibrary = await jsReader.ReadToEndAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"✓ LoadChartHtmlAsync: JS library loaded successfully! Length={_lightweightChartsLibrary.Length} bytes ({_lightweightChartsLibrary.Length / 1024}KB)");
                System.Diagnostics.Debug.WriteLine("✓ LoadChartHtmlAsync: Will inject JS library via EvaluateJavaScriptAsync after page loads (Microsoft Learn recommended pattern)");
            }
            catch (FileNotFoundException fnfEx)
            {
                System.Diagnostics.Debug.WriteLine($"✗ LoadChartHtmlAsync: JS file NOT FOUND: {fnfEx.Message}");
                System.Diagnostics.Debug.WriteLine($"✗ Expected path: Resources/Raw/lightweight-charts.standalone.production.js");
                System.Diagnostics.Debug.WriteLine($"✗ Make sure Build Action is set to MauiAsset");
                _lightweightChartsLibrary = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗ LoadChartHtmlAsync: Failed to load local JS library: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"✗ Error: {ex.Message}");
                _lightweightChartsLibrary = null;
            }

            // Remove the script tag from HTML (we'll inject the library via C# instead)
            var externalScriptTag = "<script src=\"lightweight-charts.standalone.production.js\"></script>";
            if (html.Contains(externalScriptTag))
            {
                html = html.Replace(externalScriptTag, "<!-- LightweightCharts will be injected via EvaluateJavaScriptAsync -->");
                System.Diagnostics.Debug.WriteLine($"✓ LoadChartHtmlAsync: Removed JS script tag from HTML (will inject via C# instead)");
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"LoadChartHtmlAsync: Setting WebView source (HTML size: {html.Length} bytes)");
                    
                    // Use HtmlWebViewSource (no need for temp files with small HTML)
                    ChartWebView.Source = new HtmlWebViewSource
                    {
                        Html = html
                    };
                    
                    System.Diagnostics.Debug.WriteLine($"✓ LoadChartHtmlAsync: WebView source set successfully! JS library will be injected after navigation.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✗ LoadChartHtmlAsync: Failed to set WebView source: {ex.GetType().Name} - {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"✗ Stack trace: {ex.StackTrace}");
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ LoadChartHtmlAsync: HTML file not found: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"LoadChartHtmlAsync: Using fallback HTML...");
            
            // Use a minimal working chart as fallback
            // NOTE: To fix CDN loading issues in WebView2, download the library and embed it
            // Download from: https://unpkg.com/lightweight-charts@5.0.9/dist/lightweight-charts.standalone.production.js
            // Then replace the <script src='https://unpkg.com/...'> with inline script or local file
            
            var html = @"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Chart</title>
<style>html,body{margin:0;padding:0;width:100%;height:100%;background:#0b0b0b;color:#fff;font-family:sans-serif;}#status{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);text-align:center;}</style>
</head>
<body>
<div id='chart-root' style='width:100%;height:100%'></div>
<div id='status'>Loading chart library...<br><small>(If this persists, the CDN may be blocked)</small></div>
<script src='https://unpkg.com/lightweight-charts@5.0.9/dist/lightweight-charts.standalone.production.js' onload='initChart()' onerror='showError()'></script>
<script>
console.log('Chart HTML loading...');
function showError(){
  document.getElementById('status').innerHTML='ERROR: Failed to load chart library from CDN<br><small>WebView2 may be blocking external scripts</small>';
  console.error('Failed to load LightweightCharts from CDN');
}
function initChart(){
  document.getElementById('status').style.display='none';
  console.log('LightweightCharts loaded, initializing...');
  try{
    const chart=LightweightCharts.createChart(document.getElementById('chart-root'),{layout:{background:{type:'solid',color:'#0b0b0b'},textColor:'#d7d7d7'},autoSize:true});
    const series=chart.addCandlestickSeries({upColor:'#00c853',downColor:'#ff5252',borderUpColor:'#00c853',borderDownColor:'#ff5252',wickUpColor:'#00c853',wickDownColor:'#ff5252'});
    window.marketScannerChart={setData:function(d){console.log('setData called',d);if(d&&d.candles){series.setData(d.candles);chart.timeScale().fitContent();console.log('Chart updated');}else{console.warn('No candles in payload');}}};
    console.log('Chart initialized, marketScannerChart ready');
  }catch(e){
    console.error('Error initializing chart:',e);
    document.getElementById('status').innerHTML='ERROR: '+e.message;
    document.getElementById('status').style.display='block';
  }
}
</script>
</body>
</html>";
            
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var encodedHtml = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html));
                var dataUri = $"data:text/html;base64,{encodedHtml}";
                ChartWebView.Source = dataUri;
                System.Diagnostics.Debug.WriteLine("LoadChartHtmlAsync: Fallback data URI set");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
            System.Diagnostics.Debug.WriteLine($"║ ✗✗✗ FATAL ERROR in LoadChartHtmlAsync ✗✗✗                          ║");
            System.Diagnostics.Debug.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
            System.Diagnostics.Debug.WriteLine($"Exception Type: {ex.GetType().FullName}");
            System.Diagnostics.Debug.WriteLine($"Exception Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack Trace:\n{ex.StackTrace}");
            
            if (ex.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"Inner Exception: {ex.InnerException.GetType().FullName}");
                System.Diagnostics.Debug.WriteLine($"Inner Message: {ex.InnerException.Message}");
            }
            
            // Re-throw so it gets caught by global handler
            throw;
        }
    }

    private void OnChartWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"OnChartWebViewNavigated: WebView navigated, URL={e.Url}, Success={e.Result == WebNavigationResult.Success}");
        
        // Only proceed if navigation was successful
        if (e.Result != WebNavigationResult.Success)
        {
            System.Diagnostics.Debug.WriteLine($"OnChartWebViewNavigated: Skipping - navigation failed");
            return;
        }
        
        // Skip about:blank (indicates load failure)
        if (e.Url == "about:blank")
        {
            System.Diagnostics.Debug.WriteLine($"✗ OnChartWebViewNavigated: URL is about:blank - HTML loading FAILED");
            return;
        }
        
        System.Diagnostics.Debug.WriteLine($"✓ OnChartWebViewNavigated: Valid navigation, starting EvaluateJavaScriptAsync injection...");
        
        // Use EvaluateJavaScriptAsync to inject the library after DOM loads (Microsoft Learn recommended pattern)
        // Reference: https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/javascript
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for DOM to be ready
                await Task.Delay(300);
                
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        // Step 1: Wait for document.readyState === "complete"
                        System.Diagnostics.Debug.WriteLine("OnChartWebViewNavigated: Waiting for document.readyState === 'complete'...");
                        for (int i = 0; i < 10; i++)
                        {
                            var docReady = await ChartWebView.EvaluateJavaScriptAsync("document.readyState");
                            if (docReady == "\"complete\"" || docReady == "complete")
                            {
                                System.Diagnostics.Debug.WriteLine($"✓ OnChartWebViewNavigated: Document ready after {i + 1} checks");
                                break;
                            }
                            await Task.Delay(100);
                        }
                        
                        // Step 2: Inject LightweightCharts library via EvaluateJavaScriptAsync
                        if (!string.IsNullOrEmpty(_lightweightChartsLibrary))
                        {
                            System.Diagnostics.Debug.WriteLine($"OnChartWebViewNavigated: Injecting LightweightCharts library ({_lightweightChartsLibrary.Length / 1024}KB) via EvaluateJavaScriptAsync...");
                            
                            await ChartWebView.EvaluateJavaScriptAsync(_lightweightChartsLibrary);
                            
                            System.Diagnostics.Debug.WriteLine("✓ OnChartWebViewNavigated: EvaluateJavaScriptAsync completed!");
                            
                            // Verify the library loaded
                            var libCheck = await ChartWebView.EvaluateJavaScriptAsync("typeof LightweightCharts");
                            System.Diagnostics.Debug.WriteLine($"OnChartWebViewNavigated: typeof LightweightCharts = {libCheck}");
                            
                            if (libCheck == "\"object\"" || libCheck == "object")
                            {
                                System.Diagnostics.Debug.WriteLine("✓✓✓ OnChartWebViewNavigated: LightweightCharts library successfully injected and available!");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"✗ OnChartWebViewNavigated: ERROR - LightweightCharts is {libCheck} after EvaluateJavaScriptAsync (expected 'object')");
                                return;
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("✗ OnChartWebViewNavigated: ERROR - No JS library loaded in memory to inject!");
                            return;
                        }
                        
                        // Step 3: Execute chart initialization code (initializeChart function in HTML)
                        System.Diagnostics.Debug.WriteLine("OnChartWebViewNavigated: Executing initializeChart() function...");
                        await ChartWebView.EvaluateJavaScriptAsync("if (typeof initializeChart === 'function') { initializeChart(); } else { console.error('initializeChart not found'); }");
                        
                        // Step 4: Wait for window.marketScannerChart.setData to be available
                        System.Diagnostics.Debug.WriteLine("OnChartWebViewNavigated: Waiting for window.marketScannerChart.setData...");
                        await Task.Delay(200);
                        
                        var checkScript = "typeof window.marketScannerChart !== 'undefined' && typeof window.marketScannerChart.setData === 'function' ? 'true' : 'false'";
                        var result = await ChartWebView.EvaluateJavaScriptAsync(checkScript);
                        System.Diagnostics.Debug.WriteLine($"OnChartWebViewNavigated: window.marketScannerChart.setData check: {result}");
                        
                        if (result == "true" || result == "\"true\"")
                        {
                            _chartReady = true;
                            System.Diagnostics.Debug.WriteLine("✓✓✓ OnChartWebViewNavigated: Chart is READY! _chartReady = true");
                            System.Diagnostics.Debug.WriteLine("✓✓✓ OnChartWebViewNavigated: Pushing any pending snapshot...");
                            
                            // Push any pending snapshot
                            await TryPushChartSnapshotAsync();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("✗ OnChartWebViewNavigated: ERROR - window.marketScannerChart.setData not found after initialization");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ OnChartWebViewNavigated: Exception during JS injection: {ex.GetType().Name}");
                        System.Diagnostics.Debug.WriteLine($"✗ Message: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"✗ Stack: {ex.StackTrace}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗ OnChartWebViewNavigated: Outer exception: {ex.Message}");
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.QuoteViewModel.ChartSnapshot))
        {
            System.Diagnostics.Debug.WriteLine($"OnViewModelPropertyChanged: ChartSnapshot changed, _chartReady={_chartReady}");
            _ = TryPushChartSnapshotAsync();
        }
    }

    private async Task TryPushChartSnapshotAsync()
    {
        // Store the snapshot even if chart isn't ready yet
        if (_viewModel?.ChartSnapshot != null)
        {
            _pendingSnapshot = _viewModel.ChartSnapshot;
            System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: Stored pending snapshot for {_pendingSnapshot.Symbol} ({_pendingSnapshot.Candles.Count} candles)");
        }
        
        if (!_chartReady)
        {
            System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: Chart not ready yet, snapshot stored for later (pending={_pendingSnapshot?.Symbol})");
            return;
        }

        // Use pending snapshot if current is null
        var snapshot = _viewModel?.ChartSnapshot ?? _pendingSnapshot;
        
        if (snapshot == null)
        {
            System.Diagnostics.Debug.WriteLine("TryPushChartSnapshotAsync: No snapshot available (both current and pending are null)");
            return;
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"✓ TryPushChartSnapshotAsync: Chart is ready! Pushing chart data for {snapshot.Symbol} - {snapshot.Candles.Count} candles");

            var payloadJson = JsonSerializer.Serialize(snapshot, _jsonOptions);
            System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: JSON length={payloadJson.Length}, First 200 chars: {payloadJson.Substring(0, Math.Min(200, payloadJson.Length))}");

            // Validate data before sending
            if (snapshot.Candles.Count > 0)
            {
                var firstCandle = snapshot.Candles[0];
                System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: First candle validation - Time={firstCandle.Time} (type: {firstCandle.Time.GetType().Name}), Open={firstCandle.Open}, High={firstCandle.High}, Low={firstCandle.Low}, Close={firstCandle.Close}");
                
                // Ensure time is a valid Unix timestamp
                if (firstCandle.Time <= 0)
                {
                    System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: WARNING - Invalid time value: {firstCandle.Time}");
                }
            }

            // First verify the function exists
            var checkScript = "typeof window.marketScannerChart !== 'undefined' && typeof window.marketScannerChart.setData === 'function' ? 'ready' : 'notready'";
            var checkResult = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    return await ChartWebView.EvaluateJavaScriptAsync(checkScript);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: Error checking JS function: {ex.Message}");
                    return "error";
                }
            });
            
            System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: JS function check result: {checkResult}");
            
            if (checkResult != "ready" && checkResult != "\"ready\"")
            {
                System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: JS function not ready, attempting to wait and retry...");
                await Task.Delay(500);
                
                // Try one more time
                checkResult = await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        return await ChartWebView.EvaluateJavaScriptAsync(checkScript);
                    }
                    catch
                    {
                        return "error";
                    }
                });
                
                if (checkResult != "ready" && checkResult != "\"ready\"")
                {
                    System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: ERROR - JS function still not available after retry. Check result: {checkResult}");
                    return;
                }
            }
            
            var script = $"try {{ window.marketScannerChart.setData({payloadJson}); 'success'; }} catch(e) {{ 'error: ' + e.message; }}";
            
            var result = await MainThread.InvokeOnMainThreadAsync(async () => 
            {
                try
                {
                    return await ChartWebView.EvaluateJavaScriptAsync(script);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: EvaluateJavaScriptAsync exception: {ex}");
                    throw;
                }
            });

            System.Diagnostics.Debug.WriteLine($"TryPushChartSnapshotAsync: JS evaluation result: {result}");
            
            if (result != null && result.Contains("error"))
            {
                System.Diagnostics.Debug.WriteLine($"✗ TryPushChartSnapshotAsync: ERROR - JS execution returned error: {result}");
            }
            else if (result == "success" || result == "\"success\"")
            {
                System.Diagnostics.Debug.WriteLine($"✓✓✓ TryPushChartSnapshotAsync: SUCCESS! Chart data pushed and rendered for {snapshot.Symbol}");
                _pendingSnapshot = null; // Clear pending snapshot on success
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✗ TryPushChartSnapshotAsync: EXCEPTION - Failed to push chart data: {ex}");
            System.Diagnostics.Debug.WriteLine($"✗ TryPushChartSnapshotAsync: Stack trace: {ex.StackTrace}");
        }
    }
}


using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MarketScanner.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
    public App()
    {
        this.InitializeComponent();
        
        // Add global exception handler to prevent crashes
        this.UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Log the exception with full details
        System.Diagnostics.Debug.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        System.Diagnostics.Debug.WriteLine("║ ✗✗✗ UNHANDLED EXCEPTION CAUGHT BY GLOBAL HANDLER ✗✗✗               ║");
        System.Diagnostics.Debug.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        System.Diagnostics.Debug.WriteLine($"Exception Type: {e.Exception.GetType().FullName}");
        System.Diagnostics.Debug.WriteLine($"Exception Message: {e.Exception.Message}");
        System.Diagnostics.Debug.WriteLine($"Stack Trace:\n{e.Exception.StackTrace}");
        System.Diagnostics.Debug.WriteLine($"HRESULT: {e.Exception.HResult}");
        
        if (e.Exception.InnerException != null)
        {
            System.Diagnostics.Debug.WriteLine($"\nInner Exception Type: {e.Exception.InnerException.GetType().FullName}");
            System.Diagnostics.Debug.WriteLine($"Inner Exception Message: {e.Exception.InnerException.Message}");
            System.Diagnostics.Debug.WriteLine($"Inner Stack Trace:\n{e.Exception.InnerException.StackTrace}");
        }
        
        System.Diagnostics.Debug.WriteLine("════════════════════════════════════════════════════════════════════");
        
        // Mark as handled to prevent crash
        e.Handled = true;
    }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}

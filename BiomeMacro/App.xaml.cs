using System.Windows;
using Application = System.Windows.Application;

namespace BiomeMacro;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Catch all main thread exceptions
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"An unhandled exception occurred: {args.Exception.Message}\n\nStack Trace:\n{args.Exception.StackTrace}", 
                            "RNGsus Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        
        // Catch all non-UI thread exceptions
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show($"A critical error occurred: {ex?.Message}\n\nStack Trace:\n{ex?.StackTrace}", 
                            "RNGsus Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}

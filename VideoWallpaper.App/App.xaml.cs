using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VideoWallpaper.App.Infrastructure;

namespace VideoWallpaper.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI exception.", e.Exception);
        System.Windows.MessageBox.Show(
            "An unexpected error occurred. Check the log file for details.",
            "VideoWallpaper",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        if (exception is not null)
        {
            AppLogger.Error("Unhandled non-UI exception.", exception);
        }
        else
        {
            AppLogger.Error("Unhandled non-UI exception with non-Exception payload.");
        }

        System.Windows.MessageBox.Show(
            "A fatal error occurred. Check the log file for details.",
            "VideoWallpaper",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();

        System.Windows.MessageBox.Show(
            "A background task error occurred. Check the log file for details.",
            "VideoWallpaper",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}

using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SimpleNotepad.Services;

namespace SimpleNotepad;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "SimpleNotepad.SingleInstance", out var isFirstInstance);
        _ownsMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLogger.Info($"Simple Notepad starting (v{GetType().Assembly.GetName().Version}).");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info($"Simple Notepad exiting (code {e.ApplicationExitCode}).");
        AppLogger.Shutdown();

        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI exception.", e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLogger.Error("Unhandled domain exception.", exception);
        }
        else
        {
            AppLogger.Error($"Unhandled domain exception: {e.ExceptionObject}");
        }

        AppLogger.Shutdown();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}

using System.Threading;
using System.Windows;

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

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

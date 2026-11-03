using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace G300sController;

public partial class App : Application
{
    private const string MutexName = @"Global\LogitechG300sStudio_SingleInstance_Mutex_v2";
    private const string ActivateEventName = @"Global\LogitechG300sStudio_Activate_Event_v2";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _ipcCts;

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, e.Exception.ToString());
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool isVerifyScreenshot = e.Args.Contains("--verify-screenshot", StringComparer.OrdinalIgnoreCase);

        if (!isVerifyScreenshot)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running (e.g. in System Tray). Signal it to restore and exit.
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                    evt.Set();
                }
                catch
                {
                    // Ignore if event handle is not yet ready
                }

                Shutdown(0);
                return;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _ipcCts = new CancellationTokenSource();
            CancellationToken token = _ipcCts.Token;

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (_activateEvent.WaitOne(500))
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (Current.MainWindow is MainWindow mw)
                            {
                                mw.RestoreWindowFromExternalLaunch();
                            }
                        });
                    }
                }
            }, token);
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipcCts?.Cancel();
        _ipcCts?.Dispose();
        _activateEvent?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* ignore */ }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}

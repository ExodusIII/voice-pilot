using System.Windows;

namespace VoicePilot.App;

public partial class App : Application
{
    const string MutexName = "VoicePilot.SingleInstance";
    const string ShowEventName = "VoicePilot.Show";
    const string ListenEventName = "VoicePilot.Listen";

    Mutex? _mutex;
    bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var listen = e.Args.Contains("--listen");
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Already running: forward the request to that instance.
            try { EventWaitHandle.OpenExisting(listen ? ListenEventName : ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            VoicePilot.Core.Log.Error("unhandled UI exception", ev.Exception);
            ev.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            VoicePilot.Core.Log.Error("unobserved task exception", ev.Exception);
            ev.SetObserved();
        };
        var viewIndex = Array.IndexOf(e.Args, "--view");
        var startView = viewIndex >= 0 && viewIndex + 1 < e.Args.Length ? e.Args[viewIndex + 1] : null;
        var window = new MainWindow(startHidden: e.Args.Contains("--tray"), startView);

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listenEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ListenEventName);
        new Thread(() =>
        {
            var handles = new WaitHandle[] { showEvent, listenEvent };
            while (true)
            {
                var i = WaitHandle.WaitAny(handles);
                if (i == 0) Dispatcher.InvokeAsync(window.ShowFromTray);
                else Dispatcher.InvokeAsync(window.Host.ToggleListening);
            }
        }) { IsBackground = true, Name = "InstanceSignals" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

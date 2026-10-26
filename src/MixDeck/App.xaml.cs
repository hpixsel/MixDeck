using System.Windows;
using MixDeck.Audio;
using MixDeck.Model;
using MixDeck.ViewModels;
using MixDeck.Views;

namespace MixDeck;

public partial class App : Application
{
    const string InstanceName = "MixDeck.SingleInstance";
    const string ShowEventName = "MixDeck.ShowWindow";
    const string QuitEventName = "MixDeck.Quit";
    const string FlyoutEventName = "MixDeck.Flyout";

    Mutex? _instance;
    EventWaitHandle? _showEvent;
    EventWaitHandle? _quitEvent;
    EventWaitHandle? _flyoutEvent;
    MixerService? _service;
    MainWindow? _window;
    TrayIcon? _tray;
    TrayFlyout? _flyout;
    HotkeyManager? _hotkeys;
    Osd? _osd;
    MainViewModel? _vm;
    bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out int watchedPid))
        {
            RunWatchdog(watchedPid);
            Shutdown();
            return;
        }

        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first && !e.Args.Contains("--quit") && IsOtherInstanceExiting())
        {
            // Started right after quitting: wait for the old instance to finish restoring audio, then take over
            try { first = _instance.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            // Already running: ask that instance to show its window (or to exit with --quit)
            var name = e.Args.Contains("--quit") ? QuitEventName : e.Args.Contains("--flyout") ? FlyoutEventName : ShowEventName;
            if (EventWaitHandle.TryOpenExisting(name, out var ev)) ev.Set();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--quit"))
        {
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _flyoutEvent = new EventWaitHandle(false, EventResetMode.AutoReset, FlyoutEventName);
        new Thread(() =>
        {
            while (!_exiting)
            {
                int which = WaitHandle.WaitAny([_showEvent, _quitEvent, _flyoutEvent]);
                if (_exiting) break;
                if (which == 0) Dispatcher.BeginInvoke(ShowWindow);
                else if (which == 1) Dispatcher.BeginInvoke(ExitApp);
                else Dispatcher.BeginInvoke(() => _flyout?.Toggle());
            }
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"UI error: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"Fatal: {args.ExceptionObject}");
        SessionEnding += (_, _) => ExitApp();

        Log.Write("MixDeck starting");
        _service = new MixerService(ConfigStore.Load());
        _service.Start();
        StartWatchdog();
        // An installed copy points an existing autostart entry at itself (e.g. after moving from a portable copy)
        if (Autostart.IsEnabled && new Updater().IsInstalled) Autostart.Set(true);

        _vm = new MainViewModel(_service, Dispatcher) { BeforeUpdateRestart = ShutdownForUpdate };
        _window = new MainWindow(this) { DataContext = _vm };
        _flyout = new TrayFlyout(ShowWindow) { DataContext = _vm };
        _tray = new TrayIcon(() => _flyout.Toggle(), ShowWindow, ExitApp);

        _osd = new Osd();
        _vm.OsdRequested += (glyph, accent, title, detail, level) => _osd.Show(glyph, accent, title, detail, level);
        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += h => _vm.ExecuteHotkey(h);
        _vm.HotkeysChanged += RegisterHotkeys;
        // While settings are open, recording a shortcut must not also trigger it
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SettingsOpen)) RegisterHotkeys();
        };
        RegisterHotkeys();
        if (!e.Args.Contains("--minimized")) ShowWindow();
    }

    /// <summary>
    /// A hidden second process that waits for this one to end. If it ended without a clean shutdown
    /// (crash, killed from Task Manager), it gives the Windows default output back so audio is not lost.
    /// </summary>
    static void StartWatchdog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Watchdog could not start: {ex.Message}");
        }
    }

    static void RunWatchdog(int pid)
    {
        try
        {
            using (var p = System.Diagnostics.Process.GetProcessById(pid)) p.WaitForExit();
        }
        catch (ArgumentException) { } // already gone

        Thread.Sleep(500);
        // A clean shutdown clears RestoreDefaultId; a new instance takes care of itself
        if (Mutex.TryOpenExisting(InstanceName, out var other)) { other.Dispose(); return; }
        var cfg = ConfigStore.Load();
        if (cfg?.RestoreDefaultId == null) return;

        Log.Write("Watchdog: MixDeck ended unexpectedly, restoring the default output");
        MixerService.RestoreDefaultDevice(cfg, null);
        ConfigStore.Save(cfg);
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public bool IsExiting => _exiting;

    void RegisterHotkeys()
    {
        if (_hotkeys == null || _vm == null) return;
        var active = _vm.SettingsOpen ? [] : _service!.Config.Hotkeys.Where(h => h.Gesture.Length > 0).ToList();
        _vm.MarkFailedHotkeys(_hotkeys.Register(active));
    }

    const string ExitingEventName = "MixDeck.Exiting";
    EventWaitHandle? _exitingEvent;

    static bool IsOtherInstanceExiting() =>
        EventWaitHandle.TryOpenExisting(ExitingEventName, out var ev) && ev.WaitOne(0);

    /// <summary>Tells a MixDeck started meanwhile that this instance is going away (see OnStartup).</summary>
    void AnnounceExit()
    {
        _exitingEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitingEventName);
        _exitingEvent.Set();
    }

    /// <summary>Clean shutdown before the updater restarts us (it ends the process itself).</summary>
    public void ShutdownForUpdate()
    {
        _exiting = true;
        AnnounceExit();
        Log.Write("MixDeck restarting into an update");
        _tray?.Dispose();
        _hotkeys?.Dispose();
        try { _vm?.StopRemote().Wait(3000); } catch { }
        _service?.Dispose();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        AnnounceExit();
        Log.Write("MixDeck exiting");
        _tray?.Dispose();
        _hotkeys?.Dispose();
        try { _vm?.StopRemote().Wait(3000); } catch { }
        _osd?.Close();
        _flyout?.Close();
        _service?.Dispose();
        _window?.Close();
        _showEvent?.Set();
        Shutdown();
    }
}

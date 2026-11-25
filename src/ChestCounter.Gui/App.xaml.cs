using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ChestCounter.Gui;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _wakeupCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // Global resilience hooks so transient OS/GDI/WinRT exceptions never terminate the app silently.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash("AppDomainUnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Optional automated visual verification mode: renders workspace tabs & responsive states to PNG and exits cleanly.
        int verifyIdx = Array.FindIndex(e.Args, a => a.Equals("--verify-ui", StringComparison.OrdinalIgnoreCase));
        bool isVerifyMode = verifyIdx >= 0 && verifyIdx + 1 < e.Args.Length;

        if (!isVerifyMode)
        {
            // Single-instance check: if already running (e.g. minimized to tray), wake the existing window!
            _instanceMutex = new Mutex(true, $@"Local\{AppChannel.Name}GuiSingleInstance", out bool createdNew);
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{AppChannel.Name}GuiWakeup");

            if (!createdNew)
            {
                try { _showWindowEvent.Set(); } catch { }
                Shutdown(0);
                return;
            }
        }

        base.OnStartup(e);

        var main = new MainWindow(isVerifyMode);
        MainWindow = main;
        main.Show();

        if (isVerifyMode)
        {
            string outDir = e.Args[verifyIdx + 1];
            main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () =>
            {
                await Task.Delay(900);
                try
                {
                    main.ExportWorkspaceSnapshots(outDir);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    LogCrash("ExportWorkspaceSnapshots", ex);
                    Shutdown(1);
                }
            });
            return;
        }

        _wakeupCts = new CancellationTokenSource();
        var token = _wakeupCts.Token;
        var waitHandle = _showWindowEvent!;
        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (waitHandle.WaitOne(1000))
                {
                    if (token.IsCancellationRequested) break;
                    Dispatcher.BeginInvoke(() => main.BringToFront());
                }
            }
        }, token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _wakeupCts?.Cancel(); } catch { }
        try { _showWindowEvent?.Dispose(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            var dir = AppChannel.DataDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "diagnostics.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\r\n\r\n");
        }
        catch { }
    }
}

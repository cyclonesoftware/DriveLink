using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DriveLink.Services;
using DriveLink.ViewModels;
using DriveLink.Views;

namespace DriveLink;

public partial class App : Application
{
    private TrayService _tray = null!;
    private ConfigMergeService _config = null!;
    private MountService _mountService = null!;
    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// True once an orderly shutdown has been requested (Exit button or tray Exit).
    /// Checked by MainWindow to allow the close instead of hiding to tray.
    /// </summary>
    public static bool IsShuttingDown { get; set; }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LoggingService.Info("AppStarted", $"{Branding.AppName} process started.");

        // ── Single-instance guard ──────────────────────────────────────
        // The mutex name includes the branded app name so white-label
        // forks get their own mutex and can coexist on the same machine.
        _singleInstanceMutex = new Mutex(true, @"Local\" + Branding.AppName + "_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running — activate its window and exit.
            LoggingService.Info("AppSingleInstance", "Another instance is already running; activating existing window.");
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        var systemConfig  = new SystemConfigService();
        var userConfig    = new UserConfigService();
        var brandingService = new BrandingService();
        _config           = new ConfigMergeService(systemConfig, userConfig, brandingService);
        _mountService    = new MountService(_config);

        _config.Load();

        // Apply user log retention preference (affects LoggingService cleanup).
        LoggingService.RetentionDays = _config.LogRetentionDays;

        // Fire-and-forget background refresh from the remote ConfigUrl (if configured).
        // The synchronous Load() above has already populated the connection list from local
        // sources so the UI is ready immediately; this just overlays any remote changes.
        _ = _config.StartRemoteRefreshAsync();

        _tray = new TrayService(_config, _mountService, ShowMainWindow, () =>
        {
            IsShuttingDown = true;
            Current.Shutdown();
        });

        if (_config.MergedConnections.Any(c => c.AutoConnect))
        {
            LoggingService.Info("AutoConnectStarted", "Starting auto-connect for configured connections.");
            _ = AutoConnectAtStartupAsync();
        }

        // Show the window on first run (HasLaunched not yet set) so users always
        // see the app at least once, even when system connections are pre-configured.
        // Also show when no connections exist at all (nothing to auto-connect to).
        bool isFirstRun = !_config.HasLaunched;
        if (isFirstRun)
            _config.MarkLaunched();

        if (isFirstRun || !_config.MergedConnections.Any())
            ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoggingService.Info("AppShutdown", "Application shutdown started.");
        IsShuttingDown = true;
        _mountService?.UnmountAll();
        _tray?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Find the already-running instance and bring its main window to the front.
    /// </summary>
    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            if (proc.Id == current.Id) continue;
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(proc.MainWindowHandle);
                break;
            }
        }
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            var existing = Windows.OfType<MainWindow>().FirstOrDefault();
            if (existing != null) { existing.Show(); existing.Activate(); return; }

            var vm     = new MainViewModel(_config, _mountService);
            var window = new MainWindow(vm, _config);
            // First time the user closes the window, show a one-shot balloon telling
            // them the app is still running in the tray.  Skip during shutdown so the
            // Exit button does not flash a toast notification.
            window.Closing += (_, _) => { if (!IsShuttingDown) _tray.NotifyHiddenToTray(); };
            window.Show();
        });
    }

    private async Task AutoConnectAtStartupAsync()
    {
        var failures = new List<string>();

        foreach (var profile in _config.MergedConnections
                     .Where(p => p.AutoConnect)
                     .ToList())
        {
            if (profile.RequiresSecretPrompt)
            {
                LoggingService.Info("AutoConnectSkipped",
                    "Auto-connect skipped because this connection asks for a secret at connect time.",
                    profile);
                continue;
            }

            try
            {
                await _mountService.MountAsync(profile);
            }
            catch (Exception ex)
            {
                LoggingService.Error("AutoConnectFailed", "Auto-connect failed.", ex, profile);
                failures.Add(profile.DisplayName);
            }
        }

        if (failures.Count > 0)
            _tray.NotifyAutoConnectFailed(failures);
    }

}

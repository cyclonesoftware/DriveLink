using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DriveLink.Models;
using DriveLink.Services;

namespace DriveLink.Services;

public class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ConfigMergeService _config;
    private readonly MountService _mounts;
    private readonly Action _showMainWindow;
    private readonly Action _shutdown;

    public TrayService(ConfigMergeService config, MountService mounts,
                       Action showMainWindow, Action shutdown)
    {
        _config         = config;
        _mounts         = mounts;
        _showMainWindow = showMainWindow;
        _shutdown       = shutdown;

        _icon = new NotifyIcon
        {
            Text    = _config.AppName,
            Icon    = LoadIcon(),
            Visible = true,
        };

        _icon.Click += (_, _) => _showMainWindow();
        _mounts.MountStateChanged += OnMountStateChanged;

        RebuildMenu();
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();

        foreach (var profile in _config.MergedConnections)
        {
            bool mounted = _mounts.IsMounted(profile.Id);
            var  label   = mounted
                ? $"{profile.DisplayName}  [{_mounts.GetState(profile.Id)?.DriveLetter}:]  ✓"
                : profile.DisplayName;

            var item = new ToolStripMenuItem(label);

            if (mounted)
            {
                var driveLetter = _mounts.GetState(profile.Id)?.DriveLetter;
                if (driveLetter.HasValue)
                {
                    var openItem = new ToolStripMenuItem("Open in Explorer");
                    openItem.Click += (_, _) =>
                        System.Diagnostics.Process.Start("explorer.exe", $"{driveLetter}:\\");
                    item.DropDownItems.Add(openItem);
                    item.DropDownItems.Add(new ToolStripSeparator());
                }
                var disconnectItem = new ToolStripMenuItem("Disconnect");
                disconnectItem.Click += async (_, _) =>
                {
                    await _mounts.UnmountAsync(profile.Id);
                    RebuildMenu();
                };
                item.DropDownItems.Add(disconnectItem);
            }
            else
            {
                bool needsCredentials = profile.RequiresSecretPrompt
                    || (profile.IsSystem && string.IsNullOrWhiteSpace(profile.Username));

                if (needsCredentials)
                {
                    item.Text = $"{profile.DisplayName}  ⚠ credentials needed";
                    item.ToolTipText = "Credentials required — open the app to set them";

                    var hintItem = new ToolStripMenuItem("Credentials required — open the app to set them");
                    hintItem.ForeColor = Color.FromArgb(255, 179, 64);
                    hintItem.Click += (_, _) => _showMainWindow();
                    item.DropDownItems.Add(hintItem);
                }
                else
                {
                    item.Click += async (_, _) =>
                    {
                        try { await _mounts.MountAsync(profile); RebuildMenu(); }
                        catch (Exception ex)
                        {
                            LoggingService.Error("TrayMountFailed", "Tray mount failed.", ex, profile);
                            MessageBox.Show($"Failed to mount {profile.DisplayName}:\n{ex.Message}",
                                _config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };
                }
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Manage Connections...", null, (_, _) => _showMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _shutdown());

        _icon.ContextMenuStrip = menu;
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        bool anyMounted = _config.MergedConnections.Any(p => _mounts.IsMounted(p.Id));
        _icon.Icon = LoadIcon(anyMounted);
        _icon.Text = _config.AppName;
    }

    private void OnMountStateChanged(object? sender, MountStateChangedEventArgs e)
    {
        if (App.IsShuttingDown) return;   // avoid Dispatcher.Invoke deadlock during shutdown
        System.Windows.Application.Current?.Dispatcher.Invoke(RebuildMenu);
    }

    private Icon LoadIcon(bool connected = false)
    {
        // Try system config icon path first
        if (!string.IsNullOrEmpty(_config.AppIconPath) && File.Exists(_config.AppIconPath))
        {
            try { return new Icon(_config.AppIconPath); } catch { }
        }

        // Try embedded resource
        var name = connected ? "app-icon-green" : "app-icon-blue";
        var uri  = new Uri($"pack://application:,,,/Resources/{name}.ico", UriKind.Absolute);
        try
        {
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info != null) return new Icon(info.Stream);
        }
        catch { }

        // Generate a colored circle icon — avoids blank SystemIcons.Application on modern Windows
        return CreateFallbackIcon(connected);
    }

    private static Icon CreateFallbackIcon(bool connected)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(
                connected ? Color.FromArgb(72, 199, 116) : Color.FromArgb(100, 149, 237));
            g.FillEllipse(brush, 1, 1, 13, 13);
        }
        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    // Show a one-time balloon notification the first time the main window is hidden.
    // Subsequent hides are silent — the user already knows.
    private bool _hasShownTrayHint;

    public void NotifyHiddenToTray()
    {
        if (_hasShownTrayHint) return;
        _hasShownTrayHint = true;
        _icon.ShowBalloonTip(
            timeout:  3_000,
            tipTitle: _config.AppName,
            tipText:  $"{_config.AppName} is still running in the system tray.\n" +
                      "Click the tray icon to reopen.",
            tipIcon:  ToolTipIcon.Info);
    }

    private bool _hasShownAutoConnectFailure;

    public void NotifyAutoConnectFailed(IReadOnlyList<string> failedNames)
    {
        if (failedNames.Count == 0 || _hasShownAutoConnectFailure) return;
        _hasShownAutoConnectFailure = true;

        string body = failedNames.Count == 1
            ? $"{failedNames[0]} could not be mounted. Click to open."
            : $"{failedNames.Count} connections could not be mounted. Click to open.";

        _icon.BalloonTipTitle = $"{_config.AppName} — Auto-connect failed";
        _icon.BalloonTipText  = body;
        _icon.BalloonTipIcon  = ToolTipIcon.Warning;
        _icon.BalloonTipClicked -= OnAutoConnectBalloonClicked;
        _icon.BalloonTipClicked += OnAutoConnectBalloonClicked;
        _icon.ShowBalloonTip(5_000);
    }

    private void OnAutoConnectBalloonClicked(object? sender, EventArgs e)
    {
        _showMainWindow();
        _icon.BalloonTipClicked -= OnAutoConnectBalloonClicked;
    }

    public void Dispose()
    {
        _mounts.MountStateChanged -= OnMountStateChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}

using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using DriveLink.Models;

namespace DriveLink.Services;

/// <summary>
/// Merges system and user config into a single connection list for the UI.
/// System connections come first, locked from deletion, with optional drive letter override.
/// User connections follow, fully editable.
/// </summary>
public class ConfigMergeService
{
    private readonly SystemConfigService _system;
    private readonly UserConfigService   _user;
    private readonly BrandingService     _branding;
    private readonly SynchronizationContext? _syncContext;

    // Branding comes from HKLM\SOFTWARE\DriveLink (see BrandingService).
    public string  AppName    => _branding.AppName;
    public string? AppIconPath => _branding.AppIcon;
    public string? AboutText  => _branding.AboutText;
    public string? SupportUrl => _branding.SupportUrl;
    public bool HasRemoteConfigUrl => _system.RemoteConfigUrl is not null;

    /// <summary>The resolved system-level config (after ADMX + remote merge).</summary>
    public SystemConfig SystemConfig => _system.Config;

    /// <summary>
    /// User-configurable default for hiding dot-files (Linux hidden files starting with '.').
    /// Takes precedence over SystemConfig for regular user settings.
    /// Persisted in user.json.
    /// </summary>
    public bool HideDotFiles
    {
        get => _user.Config.HideDotFiles;
        set
        {
            if (_user.Config.HideDotFiles != value)
            {
                _user.Config.HideDotFiles = value;
                _user.Save();
            }
        }
    }

    /// <summary>
    /// User preference for how many days to retain diagnostic logs.
    /// </summary>
    public int LogRetentionDays
    {
        get => _user.Config.LogRetentionDays;
        set
        {
            if (_user.Config.LogRetentionDays != value)
            {
                _user.Config.LogRetentionDays = value;
                _user.Save();
                LoggingService.RetentionDays = value;
            }
        }
    }

    public ObservableCollection<ConnectionProfile> MergedConnections { get; } = new();

    /// <summary>True once the user has seen the main window at least once.</summary>
    public bool HasLaunched => _user.Config.HasLaunched;

    /// <summary>Marks the first-run flag as complete and persists it to user.json.</summary>
    public void MarkLaunched()
    {
        if (_user.Config.HasLaunched) return;
        _user.Config.HasLaunched = true;
        _user.Save();
    }

    public ConfigMergeService(SystemConfigService system, UserConfigService user, BrandingService branding)
    {
        _system   = system;
        _user     = user;
        _branding = branding;
        _syncContext = SynchronizationContext.Current;
    }

    // -------------------------------------------------------------------------
    // Remote config refresh events
    // -------------------------------------------------------------------------

    /// <summary>
    /// Raised (on an arbitrary thread) when the background remote config download starts.
    /// Subscribers should marshal to the UI thread before updating UI state.
    /// </summary>
    public event Action? RemoteRefreshStarted;

    /// <summary>
    /// Raised (on an arbitrary thread) when the background remote config download
    /// completes — whether it succeeded or failed silently.
    /// Subscribers should marshal to the UI thread before updating UI state.
    /// </summary>
    public event Action? RemoteRefreshCompleted;

    public void Load()
    {
        LoggingService.Info("ConfigLoadStarted", "Loading branding, system config, and user config.");
        _branding.Load();
        _system.Load();
        _user.Load();
        Rebuild();
        LoggingService.Info("ConfigLoadCompleted",
            $"Loaded {MergedConnections.Count} merged connection(s); remote config configured={HasRemoteConfigUrl}.");
    }

    /// <summary>
    /// Downloads the remote system.json (if a ConfigUrl is configured) and merges
    /// the result into the live connection list.  Safe to fire-and-forget from the
    /// startup path — any error is swallowed silently and the local config is kept.
    /// Raises <see cref="RemoteRefreshStarted"/> and <see cref="RemoteRefreshCompleted"/>
    /// so the UI can display a brief "syncing" indicator.
    /// </summary>
    public async Task StartRemoteRefreshAsync()
    {
        if (_system.RemoteConfigUrl is null) return;

        RemoteRefreshStarted?.Invoke();
        LoggingService.Info("RemoteConfigRefreshStarted", "Refreshing remote system config.");
        try
        {
            var applied = await _system.MergeRemoteAsync().ConfigureAwait(false);
            await RunOnCapturedContextAsync(Rebuild).ConfigureAwait(false);
            LoggingService.Info("RemoteConfigRefreshCompleted",
                $"Remote config refresh completed; applied={applied}; merged connections={MergedConnections.Count}.");
        }
        catch (Exception ex)
        {
            // Silent — local config is already loaded and displayed.
            LoggingService.Error("RemoteConfigRefreshFailed", "Remote config refresh failed; keeping local config.", ex);
        }
        finally
        {
            RemoteRefreshCompleted?.Invoke();
        }
    }

    private Task RunOnCapturedContextAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_syncContext is null && dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(action).Task;

        if (_syncContext is null || SynchronizationContext.Current == _syncContext)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        _syncContext.Post(_ =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private void Rebuild()
    {
        MergedConnections.Clear();

        // System connections first
        foreach (var conn in _system.Config.Connections)
        {
            // Clear any credentials that may have been baked into system.json — credentials
            // are always supplied by the user and stored in user.json (see UserCredential).
            conn.Username                      = string.Empty;
            conn.PasswordEncrypted             = null;
            conn.PrivateKeyPath                = null;
            conn.PrivateKeyPassphraseEncrypted = null;
            conn.SecretStorageMode             = SecretStorageMode.Save;

            // Apply the user's credentials for this system connection (if set).
            var cred = _user.Config.Credentials.FirstOrDefault(c => c.ProfileId == conn.Id);
            if (cred != null)
            {
                conn.Username                      = cred.Username                      ?? string.Empty;
                conn.PasswordEncrypted             = cred.PasswordEncrypted;
                conn.PrivateKeyPath                = cred.PrivateKeyPath;
                conn.PrivateKeyPassphraseEncrypted = cred.PrivateKeyPassphraseEncrypted;
                conn.SecretStorageMode             = cred.SecretStorageMode;
                if (cred.HostKeyFingerprint != null)
                {
                    conn.HostKeyFingerprint = string.IsNullOrWhiteSpace(cred.HostKeyFingerprint)
                        ? null
                        : cred.HostKeyFingerprint;
                }
            }

            // Apply user drive letter override if permitted.
            if (conn.AllowDriveLetterOverride &&
                _user.Config.DriveLetterOverrides.TryGetValue(conn.Id, out var letter))
            {
                conn.PreferredDriveLetter = letter;
            }

            // Apply user auto-connect override if set.
            if (_user.Config.AutoConnectOverrides.TryGetValue(conn.Id, out var autoConnect))
                conn.AutoConnect = autoConnect;

            MergedConnections.Add(conn);
        }

        // User connections (credentials are stored directly on the profile).
        foreach (var conn in _user.Config.Connections)
            MergedConnections.Add(conn);
    }

    // -------------------------------------------------------------------------
    // Delegated write operations (user config only)
    // -------------------------------------------------------------------------

    public void AddUserConnection(ConnectionProfile profile)
    {
        _user.AddConnection(profile);
        MergedConnections.Add(profile);
    }

    public void UpdateUserConnection(ConnectionProfile profile)
    {
        _user.UpdateConnection(profile);
        var idx = IndexOf(profile.Id);
        if (idx >= 0) MergedConnections[idx] = profile;
    }

    public void RemoveUserConnection(string id)
    {
        _user.RemoveConnection(id);
        var existing = MergedConnections.FirstOrDefault(c => c.Id == id);
        if (existing != null) MergedConnections.Remove(existing);
    }

    /// <summary>
    /// Persists the user's credentials for a system connection and applies them
    /// to the live merged profile in-place (no full Rebuild required).
    /// </summary>
    public void SaveSystemCredentials(UserCredential cred)
    {
        _user.SaveCredential(cred);

        var conn = MergedConnections.FirstOrDefault(c => c.Id == cred.ProfileId);
        if (conn is null) return;

        conn.Username                      = cred.Username                      ?? string.Empty;
        conn.PasswordEncrypted             = cred.PasswordEncrypted;
        conn.PrivateKeyPath                = cred.PrivateKeyPath;
        conn.PrivateKeyPassphraseEncrypted = cred.PrivateKeyPassphraseEncrypted;
        conn.SecretStorageMode             = cred.SecretStorageMode;
        if (cred.HostKeyFingerprint != null)
        {
            conn.HostKeyFingerprint = string.IsNullOrWhiteSpace(cred.HostKeyFingerprint)
                ? null
                : cred.HostKeyFingerprint;
        }
    }

    public void SaveHostKeyFingerprint(ConnectionProfile profile, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return;

        profile.HostKeyFingerprint = fingerprint;

        if (profile.IsSystem)
        {
            _user.SetCredentialHostKeyFingerprint(profile.Id, fingerprint);
        }
        else
        {
            _user.SetConnectionHostKeyFingerprint(profile.Id, fingerprint);
        }

        var conn = MergedConnections.FirstOrDefault(c => c.Id == profile.Id);
        if (conn != null)
            conn.HostKeyFingerprint = fingerprint;
    }

    public void ClearHostKeyFingerprint(ConnectionProfile profile)
    {
        profile.HostKeyFingerprint = null;

        if (profile.IsSystem)
            _user.SetCredentialHostKeyFingerprint(profile.Id, string.Empty);
        else
            _user.UpdateConnection(profile);

        var conn = MergedConnections.FirstOrDefault(c => c.Id == profile.Id);
        if (conn != null)
            conn.HostKeyFingerprint = null;
    }

    /// <summary>
    /// Saves a user drive letter override for a system connection.
    /// </summary>
    public void SetSystemConnectionDriveLetter(string profileId, char letter)
    {
        _user.SetDriveLetterOverride(profileId, letter);
        var conn = MergedConnections.FirstOrDefault(c => c.Id == profileId);
        if (conn != null) conn.PreferredDriveLetter = letter;
    }

    public void ClearSystemConnectionDriveLetter(string profileId)
    {
        _user.ClearDriveLetterOverride(profileId);
        Rebuild();
    }

    /// <summary>
    /// Toggles auto-connect for any connection and persists the change.
    /// For user connections the full profile is saved; for system connections
    /// an override entry is written to user.json (consistent with drive letter overrides).
    /// </summary>
    public void SetAutoConnect(string profileId, bool value)
    {
        var conn = MergedConnections.FirstOrDefault(c => c.Id == profileId);
        if (conn == null) return;

        conn.AutoConnect = value;

        if (conn.IsSystem)
            _user.SetAutoConnectOverride(profileId, value);
        else
            _user.UpdateConnection(conn);
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < MergedConnections.Count; i++)
            if (MergedConnections[i].Id == id) return i;
        return -1;
    }
}

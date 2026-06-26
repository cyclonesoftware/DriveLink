using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DriveLink.Helpers;
using DriveLink.Models;
using DriveLink.Services;

namespace DriveLink.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ConfigMergeService _config;
    private readonly MountService _mounts;
    private readonly SshfsWinManagerImportService _importService;
    private ConnectionViewModel? _selectedConnection;

    public string  AppName    => _config.AppName;
    public string? AboutText  => _config.AboutText;
    public string? SupportUrl => _config.SupportUrl;

    /// <summary>
    /// True when the app is running with default "DriveLink" branding, meaning no
    /// organisation has customised it via APPNAME installer property or GPO registry key.
    /// Used to show the donation/customisation panel in the About dialog.
    /// </summary>
    public bool IsDefaultBranding => AppName == Branding.AppName;

    // Version comes from the assembly — set <Version> in the .csproj to control this.
    public string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

    public ConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            _selectedConnection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanDuplicate));
            OnPropertyChanged(nameof(CanSetCredentials));
            OnPropertyChanged(nameof(ShowSetCredentials));
            OnPropertyChanged(nameof(SelectedNeedsCredentials));
        }
    }

    /// <summary>
    /// True when an SSHFS-Win Manager connections file is present, controlling
    /// visibility of the "Import from SSHFS-Win Manager…" sidebar button.
    /// Re-evaluated whenever the main window is activated (see RefreshCanImport)
    /// so the button can appear if the user installs SSHFS-Win Manager later.
    /// </summary>
    public bool CanImport => _importService.ConfigExists();

    public bool CanEdit           => SelectedConnection?.IsUserOwned == true;
    public bool CanDelete         => SelectedConnection?.IsUserOwned == true;
    public bool CanDuplicate      => SelectedConnection != null;
    /// <summary>True when a system connection is selected (credentials can be set/updated).</summary>
    public bool CanSetCredentials => SelectedConnection?.IsSystem    == true;

    /// <summary>
    /// Controls visibility of the "Set Credentials" sidebar button. The scoped
    /// credentials editor only applies to system connections — user connections edit
    /// their credentials inline in EditConnectionWindow — so the button is collapsed
    /// for user connections (and when nothing is selected) to avoid two buttons that
    /// both appear to edit the connection.
    /// </summary>
    public bool ShowSetCredentials => SelectedConnection?.IsSystem == true;

    /// <summary>
    /// True when the selected system connection still needs credentials, used to show
    /// an amber indicator marking "Set Credentials" as the obvious next action.
    /// </summary>
    public bool SelectedNeedsCredentials => SelectedConnection?.NeedsCredentials == true;

    /// <summary>
    /// True while the background remote config download is in progress.
    /// Bound to the "Syncing remote config…" indicator in the connection list panel.
    /// </summary>
    public bool IsRemoteRefreshing { get; private set; }

    public bool AutoRun
    {
        get => AutoRunHelper.IsEnabled();
        set { if (value) AutoRunHelper.Enable(); else AutoRunHelper.Disable(); OnPropertyChanged(); }
    }

    public ICommand ToggleMountCommand   { get; }
    public ICommand ReconnectCommand     { get; }
    public ICommand AddCommand           { get; }
    public ICommand ImportCommand        { get; }
    public ICommand EditCommand          { get; }
    public ICommand DuplicateCommand     { get; }
    public ICommand DeleteCommand        { get; }
    public ICommand SetCredentialsCommand { get; }
    public ICommand ViewLogsCommand      { get; }
    public ICommand CopySupportDetailsCommand { get; }

    public MainViewModel(ConfigMergeService config, MountService mounts)
    {
        _config = config;
        _mounts = mounts;
        _importService = new SshfsWinManagerImportService();

        foreach (var p in _config.MergedConnections)
            Connections.Add(new ConnectionViewModel(p, _mounts, _config.SetAutoConnect));

        _config.MergedConnections.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ConnectionProfile p in e.NewItems)
                    Connections.Add(new ConnectionViewModel(p, _mounts, _config.SetAutoConnect));
            if (e.OldItems != null)
                foreach (ConnectionProfile p in e.OldItems)
                {
                    var vm = Connections.FirstOrDefault(c => c.Id == p.Id);
                    if (vm != null) Connections.Remove(vm);
                }
        };

        ToggleMountCommand = new AsyncRelayCommand<ConnectionViewModel>(async vm =>
        {
            // A dropped (connection-lost) mount still holds the WinFsp host and drive
            // letter, so the toggle acts as a clean Disconnect in that state too.
            if (vm!.IsMounted || vm.IsConnectionLost)
            {
                await _mounts.UnmountAsync(vm.Id);
                return;
            }

            // System connection with no credentials — prompt before attempting to mount.
            if (vm.NeedsCredentials)
            {
                OpenCredentialsDialog?.Invoke(vm.Profile);
                return;
            }

            await MountWithCredentialPromptAsync(vm);
        });

        ReconnectCommand = new AsyncRelayCommand<ConnectionViewModel>(async vm =>
        {
            // Recover a dropped mount in one click: clean up the dead session
            // (releasing the WinFsp host and drive letter), then re-SSH and re-mount.
            await _mounts.UnmountAsync(vm!.Id);

            if (vm.NeedsCredentials)
            {
                OpenCredentialsDialog?.Invoke(vm.Profile);
                return;
            }

            await MountWithCredentialPromptAsync(vm);
        });

        AddCommand            = new RelayCommand(_ => OpenConnectionDialog?.Invoke(null));
        ImportCommand         = new RelayCommand(_ => OpenImportDialog?.Invoke());
        EditCommand           = new RelayCommand(p =>
        {
            var target = p as ConnectionViewModel ?? SelectedConnection;
            if (target != null)
                OpenConnectionDialog?.Invoke(target.Profile);
        }, _ => CanEdit);
        DuplicateCommand      = new RelayCommand(p =>
        {
            var target = p as ConnectionViewModel ?? SelectedConnection;
            DuplicateSelected(target);
        }, _ => CanDuplicate);
        DeleteCommand         = new RelayCommand(p =>
        {
            var target = p as ConnectionViewModel ?? SelectedConnection;
            DeleteSelected(target);
        }, _ => CanDelete);
        SetCredentialsCommand = new RelayCommand(p =>
        {
            var target = p as ConnectionViewModel ?? SelectedConnection;
            if (target != null)
                OpenCredentialsDialog?.Invoke(target.Profile);
        }, _ => CanSetCredentials);
        ViewLogsCommand       = new RelayCommand(_ => OpenLogFolderRequested?.Invoke(LoggingService.LogDirectory));
        CopySupportDetailsCommand = new RelayCommand(_ =>
            CopySupportDetailsRequested?.Invoke(
                LoggingService.BuildSupportDetails(_config, SelectedConnection?.Profile)));

        // Subscribe to remote refresh events from ConfigMergeService.
        // Events fire on a background thread — marshal to UI thread before raising INPC.
        _config.RemoteRefreshStarted += () =>
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsRemoteRefreshing = true;
                OnPropertyChanged(nameof(IsRemoteRefreshing));
            });

        _config.RemoteRefreshCompleted += () =>
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsRemoteRefreshing = false;
                OnPropertyChanged(nameof(IsRemoteRefreshing));
            });
    }

    public event Action? OpenImportDialog;
    public event Action<ConnectionProfile?>?  OpenConnectionDialog;
    public event Action<ConnectionProfile>?   OpenDuplicateDialog;
    public event Action<ConnectionProfile>?   OpenCredentialsDialog;
    public event Func<ConnectionProfile, ConnectionProfile?>? RequestSecretPrompt;
    public event Action<string>? OpenLogFolderRequested;
    public event Action<string>? CopySupportDetailsRequested;

    /// <summary>
    /// Raised on the UI thread when a mount attempt fails.
    /// The string argument is a human-readable error message ready to display.
    /// </summary>
    public event Action<string>? MountError;

    /// <summary>
    /// Persists the user's credentials for a system connection and refreshes
    /// the matching <see cref="ConnectionViewModel"/> so the UI reflects the change.
    /// </summary>
    public void SaveSystemCredentials(UserCredential cred)
    {
        _config.SaveSystemCredentials(cred);
        var vm = Connections.FirstOrDefault(c => c.Id == cred.ProfileId);
        vm?.RefreshNeedsCredentials();
        // Refresh the sidebar amber indicator for the (still-selected) connection.
        OnPropertyChanged(nameof(SelectedNeedsCredentials));
    }

    /// <summary>
    /// Re-evaluates whether the SSHFS-Win Manager import button should be shown.
    /// Called when the main window is activated so installing SSHFS-Win Manager
    /// after launch makes the button appear without a restart.
    /// </summary>
    public void RefreshCanImport() => OnPropertyChanged(nameof(CanImport));

    /// <summary>
    /// Reads importable connections from the SSHFS-Win Manager config.
    /// Throws <see cref="InvalidOperationException"/> with a user-facing message
    /// on parse failure — callers must abort and make no changes.
    /// </summary>
    public List<ConnectionProfile> ReadImportCandidates() => _importService.ReadConnections();

    /// <summary>
    /// Adds the chosen imported connections to user config, skipping any that
    /// duplicate an existing connection (same Host + Port + RemotePath + Username).
    /// Returns how many were imported and how many were skipped as duplicates.
    /// </summary>
    public (int imported, int skipped) ImportConnections(IEnumerable<ConnectionProfile> selected)
    {
        int imported = 0, skipped = 0;
        foreach (var profile in selected)
        {
            if (IsDuplicate(profile)) { skipped++; continue; }
            _config.AddUserConnection(profile);
            imported++;
        }
        return (imported, skipped);
    }

    private bool IsDuplicate(ConnectionProfile candidate) =>
        _config.MergedConnections.Any(existing =>
            string.Equals(existing.Host, candidate.Host, StringComparison.OrdinalIgnoreCase) &&
            existing.Port == candidate.Port &&
            string.Equals(existing.RemotePath, candidate.RemotePath, StringComparison.Ordinal) &&
            string.Equals(existing.Username, candidate.Username, StringComparison.Ordinal));

    /// <summary>
    /// Shared connect path used by both the normal mount toggle and Reconnect:
    /// honours "Ask when connecting" by prompting for the secret, drives the
    /// connecting spinner, and surfaces mount errors to the user.
    /// </summary>
    private async Task MountWithCredentialPromptAsync(ConnectionViewModel vm)
    {
        var profileToMount = vm.Profile;
        if (vm.Profile.RequiresSecretPrompt)
        {
            var promptedProfile = RequestSecretPrompt?.Invoke(vm.Profile);
            if (promptedProfile is null)
                return;
            profileToMount = promptedProfile;
        }

        vm.IsConnecting = true;
        try
        {
            await _mounts.MountAsync(profileToMount);
        }
        catch (Exception ex)
        {
            // Surface connection errors to the user.  The inner message is
            // already human-readable (translated by MountService.MountAsync).
            MountError?.Invoke(ex.Message);
        }
        finally
        {
            vm.IsConnecting = false;
        }
    }

    public void SaveProfile(ConnectionProfile profile)
    {
        if (_config.MergedConnections.Any(p => p.Id == profile.Id))
            _config.UpdateUserConnection(profile);
        else
            _config.AddUserConnection(profile);
    }

    public void AddDuplicatedProfile(ConnectionProfile profile) =>
        _config.AddUserConnection(profile);

    private void DuplicateSelected(ConnectionViewModel? target = null)
    {
        target ??= SelectedConnection;
        if (target == null) return;
        OpenDuplicateDialog?.Invoke(CreateDuplicateProfile(target.Profile));
    }

    private static ConnectionProfile CreateDuplicateProfile(ConnectionProfile source)
    {
        bool copySecrets = !source.IsSystem;
        var displayName = string.IsNullOrWhiteSpace(source.DisplayName)
            ? "Connection Copy"
            : $"{source.DisplayName} Copy";

        return new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            PasswordEncrypted = copySecrets ? source.PasswordEncrypted : null,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphraseEncrypted = copySecrets ? source.PrivateKeyPassphraseEncrypted : null,
            HostKeyFingerprint = source.HostKeyFingerprint,
            SecretStorageMode = copySecrets ? source.SecretStorageMode : SecretStorageMode.AskAtConnect,
            RemotePath = source.RemotePath,
            PreferredDriveLetter = null,
            VolumeLabel = displayName,
            AutoConnect = false,
            ReadOnlyMount = source.ReadOnlyMount,
            CacheDurationSeconds = source.CacheDurationSeconds,
            ConnectionTimeoutSeconds = source.ConnectionTimeoutSeconds,
            OperationTimeoutSeconds = source.OperationTimeoutSeconds,
            HideDotFilesOverride = source.HideDotFilesOverride,
            IsSystem = false,
            AllowDriveLetterOverride = true,
        };
    }

    private void DeleteSelected(ConnectionViewModel? target = null)
    {
        target ??= SelectedConnection;
        if (target == null || !target.IsUserOwned) return;
        _mounts.UnmountAsync(target.Id).GetAwaiter().GetResult();
        _config.RemoveUserConnection(target.Id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

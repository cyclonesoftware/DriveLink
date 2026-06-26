using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DriveLink.Models;
using DriveLink.Services;

namespace DriveLink.ViewModels;

public class ConnectionViewModel : INotifyPropertyChanged
{
    private readonly MountService          _mounts;
    private readonly Action<string, bool>? _onAutoConnectChanged;
    private bool _isMounted;
    private bool _isConnecting;
    private bool _isConnectionLost;

    public ConnectionProfile Profile { get; }

    public string Id            => Profile.Id;
    public string DisplayName   => Profile.DisplayName;
    public string Host          => Profile.Host;
    public bool   IsSystem      => Profile.IsSystem;
    public bool   IsUserOwned   => !Profile.IsSystem;
    public bool   CanEditDriveLetter => Profile.IsSystem && Profile.AllowDriveLetterOverride;

    /// <summary>
    /// Per-item permission flags for use in context menus (independent of SelectedConnection).
    /// </summary>
    public bool CanEditThis   => !IsSystem;
    public bool CanDeleteThis => !IsSystem;

    /// <summary>
    /// True when this is a system connection and the user has not yet supplied
    /// credentials.  Connecting without credentials would fail immediately.
    /// </summary>
    public bool NeedsCredentials => Profile.IsSystem && string.IsNullOrWhiteSpace(Profile.Username);
    public bool NeedsSecretPrompt => Profile.RequiresSecretPrompt;

    /// <summary>Raises PropertyChanged for NeedsCredentials (and dependent properties) after credentials are saved.</summary>
    public void RefreshNeedsCredentials()
    {
        OnPropertyChanged(nameof(NeedsCredentials));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConnectButtonTooltip));
        OnPropertyChanged(nameof(NeedsSecretPrompt));
    }

    public string DriveLetterDisplay =>
        Profile.PreferredDriveLetter.HasValue ? $"{Profile.PreferredDriveLetter.Value}:" : "Auto";

    public bool IsMounted
    {
        get => _isMounted;
        set
        {
            _isMounted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MountedDriveText));
            OnPropertyChanged(nameof(ConnectButtonTooltip));
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            _isConnecting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectButtonTooltip));
        }
    }

    /// <summary>
    /// True when the SSH session dropped while mounted. Surfaces the "Connection lost"
    /// status and the Reconnect button (see <see cref="MainViewModel.ReconnectCommand"/>).
    /// </summary>
    public bool IsConnectionLost
    {
        get => _isConnectionLost;
        set
        {
            _isConnectionLost = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectButtonTooltip));
        }
    }

    public bool   IsDisconnected => !_isMounted && !_isConnecting && !_isConnectionLost;
    public string StatusText     => _isMounted        ? "Connected"         :
                                    _isConnecting     ? "Connecting…"       :
                                    _isConnectionLost ? "Connection lost"   :
                                    NeedsCredentials  ? "Credentials needed" :
                                    NeedsSecretPrompt ? "Ready - will ask"   : "Disconnected";

    public string MountedDriveText
    {
        get
        {
            var letter = _mounts.GetState(Id)?.DriveLetter;
            return letter.HasValue ? $"· {letter.Value}:" : string.Empty;
        }
    }

    public string ConnectButtonTooltip =>
        _isMounted        ? "Disconnect"           :
        _isConnecting     ? "Connecting…"          :
        _isConnectionLost ? "Disconnect"           :
        NeedsCredentials  ? "Set credentials first" :
        NeedsSecretPrompt ? "Connect and enter secret" : "Connect";

    /// <summary>
    /// Whether this connection should mount automatically on application startup.
    /// Toggling persists immediately via the callback supplied at construction.
    /// </summary>
    public bool AutoConnect
    {
        get => Profile.AutoConnect;
        set
        {
            Profile.AutoConnect = value;
            OnPropertyChanged();
            _onAutoConnectChanged?.Invoke(Profile.Id, value);
        }
    }

    public ICommand OpenInExplorerCommand { get; }

    public ConnectionViewModel(ConnectionProfile profile, MountService mounts,
                               Action<string, bool>? onAutoConnectChanged = null)
    {
        Profile               = profile;
        _mounts               = mounts;
        _onAutoConnectChanged = onAutoConnectChanged;
        _isMounted            = mounts.IsMounted(profile.Id);
        _isConnectionLost     = mounts.GetState(profile.Id)?.IsConnectionLost ?? false;

        OpenInExplorerCommand = new RelayCommand(_ =>
        {
            var letter = _mounts.GetState(Id)?.DriveLetter;
            if (letter.HasValue)
                Process.Start(new ProcessStartInfo($"{letter.Value}:\\")
                    { UseShellExecute = true });
        });

        _mounts.MountStateChanged += (_, e) =>
        {
            if (e.State.ProfileId == Id)
            {
                // Set the lost flag first so StatusText is correct when IsMounted changes.
                IsConnectionLost = e.State.IsConnectionLost;
                IsMounted = e.State.IsMounted;
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

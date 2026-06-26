using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DriveLink.Helpers;
using DriveLink.Models;

namespace DriveLink.ViewModels;

public class EditConnectionViewModel : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _host        = string.Empty;
    private int    _port        = 22;
    private string _username    = string.Empty;
    private string _password    = string.Empty;
    private string _privateKeyPath = string.Empty;
    private string _privateKeyPassphrase = string.Empty;
    private string _remotePath  = ".";
    private bool   _useKey;
    private bool   _autoConnect;
    private bool   _autoLetter = true;
    private char?  _selectedLetter;
    private string? _hostKeyFingerprint;
    private SecretStorageMode _secretStorageMode = SecretStorageMode.Save;
    private string _volumeLabel = string.Empty;
    private bool _readOnlyMount;
    private int _cacheDurationSeconds = 15;
    private int _connectionTimeoutSeconds = 15;
    private int _operationTimeoutSeconds = 30;
    private bool? _hideDotFilesOverride;

    public string Id { get; } = Guid.NewGuid().ToString();

    public string DisplayName          { get => _displayName; set { _displayName = value; OnPropertyChanged(); } }
    public string Host                 { get => _host;        set { _host = value; OnPropertyChanged(); } }
    public int    Port                 { get => _port;        set { _port = value; OnPropertyChanged(); } }
    public string Username             { get => _username;    set { _username = value; OnPropertyChanged(); } }
    public string Password             { get => _password;    set { _password = value; OnPropertyChanged(); } }
    public string PrivateKeyPath       { get => _privateKeyPath; set { _privateKeyPath = value; OnPropertyChanged(); } }
    public string PrivateKeyPassphrase { get => _privateKeyPassphrase; set { _privateKeyPassphrase = value; OnPropertyChanged(); } }
    public string RemotePath           { get => _remotePath;  set { _remotePath = value; OnPropertyChanged(); } }
    public string? HostKeyFingerprint
    {
        get => _hostKeyFingerprint;
        set
        {
            _hostKeyFingerprint = NormalizeHostKeyFingerprint(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HostKeyStatus));
            OnPropertyChanged(nameof(HasHostKeyFingerprint));
        }
    }
    public bool HasHostKeyFingerprint => !string.IsNullOrWhiteSpace(HostKeyFingerprint);
    public string HostKeyStatus => HasHostKeyFingerprint
        ? $"Trusted: {HostKeyFingerprint}"
        : "Not trusted yet";
    public SecretStorageMode SecretStorageMode
    {
        get => _secretStorageMode;
        set
        {
            _secretStorageMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SaveSecret));
            OnPropertyChanged(nameof(AskAtConnect));
        }
    }
    public bool SaveSecret
    {
        get => SecretStorageMode == SecretStorageMode.Save;
        set { if (value) SecretStorageMode = SecretStorageMode.Save; }
    }
    public bool AskAtConnect
    {
        get => SecretStorageMode == SecretStorageMode.AskAtConnect;
        set { if (value) SecretStorageMode = SecretStorageMode.AskAtConnect; }
    }
    public bool   AutoConnect          { get => _autoConnect; set { _autoConnect = value; OnPropertyChanged(); } }
    public char?  SelectedLetter       { get => _selectedLetter; set { _selectedLetter = value; OnPropertyChanged(); } }
    public string VolumeLabel          { get => _volumeLabel; set { _volumeLabel = value; OnPropertyChanged(); } }
    public bool   ReadOnlyMount        { get => _readOnlyMount; set { _readOnlyMount = value; OnPropertyChanged(); } }
    public int CacheDurationSeconds
    {
        get => _cacheDurationSeconds;
        set { _cacheDurationSeconds = value; OnPropertyChanged(); }
    }
    public int ConnectionTimeoutSeconds
    {
        get => _connectionTimeoutSeconds;
        set { _connectionTimeoutSeconds = value; OnPropertyChanged(); }
    }
    public int OperationTimeoutSeconds
    {
        get => _operationTimeoutSeconds;
        set { _operationTimeoutSeconds = value; OnPropertyChanged(); }
    }
    public bool? HideDotFilesOverride
    {
        get => _hideDotFilesOverride;
        set
        {
            _hideDotFilesOverride = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseDefaultDotFileSetting));
            OnPropertyChanged(nameof(HideDotFilesForConnection));
            OnPropertyChanged(nameof(ShowDotFilesForConnection));
        }
    }

    public bool UseDefaultDotFileSetting
    {
        get => HideDotFilesOverride is null;
        set { if (value) HideDotFilesOverride = null; }
    }
    public bool HideDotFilesForConnection
    {
        get => HideDotFilesOverride == true;
        set { if (value) HideDotFilesOverride = true; }
    }
    public bool ShowDotFilesForConnection
    {
        get => HideDotFilesOverride == false;
        set { if (value) HideDotFilesOverride = false; }
    }

    public bool UseKey
    {
        get => _useKey;
        set { _useKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsePassword)); }
    }
    public bool UsePassword
    {
        get => !_useKey;
        set { _useKey = !value; OnPropertyChanged(); OnPropertyChanged(nameof(UseKey)); }
    }

    public bool AutoLetter
    {
        get => _autoLetter;
        set { _autoLetter = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManualLetter)); }
    }
    public bool ManualLetter
    {
        get => !_autoLetter;
        set { _autoLetter = !value; OnPropertyChanged(); OnPropertyChanged(nameof(AutoLetter)); }
    }

    public ObservableCollection<char> AvailableLetters { get; } = new();

    public EditConnectionViewModel() => RefreshAvailableLetters();

    public void RefreshAvailableLetters()
    {
        AvailableLetters.Clear();
        foreach (var c in DriveLetterHelper.GetAvailable())
            AvailableLetters.Add(c);
        SelectedLetter = AvailableLetters.FirstOrDefault();
    }

    public static EditConnectionViewModel FromProfile(ConnectionProfile profile) => new()
    {
        DisplayName          = profile.DisplayName,
        Host                 = profile.Host,
        Port                 = profile.Port,
        Username             = profile.Username,
        Password             = CredentialHelper.Decrypt(profile.PasswordEncrypted),
        UseKey               = profile.AuthMode == AuthMode.PrivateKey,
        PrivateKeyPath       = profile.PrivateKeyPath ?? string.Empty,
        PrivateKeyPassphrase = CredentialHelper.Decrypt(profile.PrivateKeyPassphraseEncrypted),
        RemotePath           = profile.RemotePath,
        HostKeyFingerprint   = profile.HostKeyFingerprint,
        SecretStorageMode    = profile.SecretStorageMode,
        AutoConnect          = profile.AutoConnect,
        AutoLetter           = !profile.PreferredDriveLetter.HasValue,
        SelectedLetter       = profile.PreferredDriveLetter,
        VolumeLabel          = profile.VolumeLabel ?? string.Empty,
        ReadOnlyMount        = profile.ReadOnlyMount,
        CacheDurationSeconds = profile.CacheDurationSeconds,
        ConnectionTimeoutSeconds = profile.ConnectionTimeoutSeconds,
        OperationTimeoutSeconds  = profile.OperationTimeoutSeconds,
        HideDotFilesOverride = profile.HideDotFilesOverride,
    };

    public ConnectionProfile ToProfile(string? existingId = null) => new()
    {
        Id                            = existingId ?? Id,
        DisplayName                   = DisplayName,
        Host                          = Host,
        Port                          = Port,
        Username                      = Username,
        PasswordEncrypted             = UsePassword && SecretStorageMode == SecretStorageMode.Save
            ? CredentialHelper.Encrypt(Password)
            : null,
        PrivateKeyPath                = UseKey ? PrivateKeyPath : null,
        PrivateKeyPassphraseEncrypted = UseKey && SecretStorageMode == SecretStorageMode.Save
            ? CredentialHelper.Encrypt(PrivateKeyPassphrase)
            : null,
        HostKeyFingerprint            = HostKeyFingerprint,
        SecretStorageMode             = SecretStorageMode,
        RemotePath                    = RemotePath,
        AutoConnect                   = AutoConnect,
        PreferredDriveLetter          = ManualLetter ? SelectedLetter : null,
        VolumeLabel                   = string.IsNullOrWhiteSpace(VolumeLabel) ? DisplayName : VolumeLabel.Trim(),
        ReadOnlyMount                 = ReadOnlyMount,
        CacheDurationSeconds          = CacheDurationSeconds,
        ConnectionTimeoutSeconds      = ConnectionTimeoutSeconds,
        OperationTimeoutSeconds       = OperationTimeoutSeconds,
        HideDotFilesOverride          = HideDotFilesOverride,
        IsSystem                      = false,
    };

    public void ClearHostKeyFingerprint() => HostKeyFingerprint = null;

    private static string? NormalizeHostKeyFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            value = value["SHA256:".Length..];
        return "SHA256:" + value.Trim().TrimEnd('=');
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

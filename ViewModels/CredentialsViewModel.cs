using System.ComponentModel;
using System.Runtime.CompilerServices;
using DriveLink.Helpers;
using DriveLink.Models;

namespace DriveLink.ViewModels;

/// <summary>
/// ViewModel for <see cref="Views.CredentialsWindow"/>.
/// Holds user-supplied credentials for a system-defined connection.
/// Server fields (host, port, remote path) are read-only display values;
/// only username and auth fields are editable.
/// </summary>
public class CredentialsViewModel : INotifyPropertyChanged
{
    private string _username       = string.Empty;
    private string _privateKeyPath = string.Empty;
    private bool   _useKey;
    private string? _hostKeyFingerprint;
    private bool _hostKeyFingerprintCleared;
    private SecretStorageMode _secretStorageMode = SecretStorageMode.Save;

    // ── Read-only connection info (set by IT in system.json) ────────────────

    public string ProfileId   { get; }
    public string DisplayName { get; }
    public string HostDisplay { get; }   // "host : port"
    public string RemotePath  { get; }
    public string? HostKeyFingerprint
    {
        get => _hostKeyFingerprint;
        set
        {
            _hostKeyFingerprint = NormalizeHostKeyFingerprint(value);
            if (_hostKeyFingerprint != null)
                _hostKeyFingerprintCleared = false;
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

    // ── Editable credentials ─────────────────────────────────────────────────

    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set { _privateKeyPath = value; OnPropertyChanged(); }
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

    // Populated from PasswordBox controls in code-behind (not data-bound).
    public string Password             { get; set; } = string.Empty;
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    // ── Construction ─────────────────────────────────────────────────────────

    public CredentialsViewModel(ConnectionProfile profile)
    {
        ProfileId   = profile.Id;
        DisplayName = profile.DisplayName;
        HostDisplay = $"{profile.Host} : {profile.Port}";
        RemotePath  = profile.RemotePath;

        // Pre-populate if the user has previously saved credentials.
        Username       = profile.Username;
        UseKey         = profile.AuthMode == AuthMode.PrivateKey;
        PrivateKeyPath = profile.PrivateKeyPath ?? string.Empty;
        HostKeyFingerprint = profile.HostKeyFingerprint;
        SecretStorageMode = profile.SecretStorageMode;

        // Password and passphrase are populated from PasswordBoxes by the window.
    }

    // ── Output ───────────────────────────────────────────────────────────────

    public UserCredential ToUserCredential() => new()
    {
        ProfileId                    = ProfileId,
        Username                     = Username,
        PasswordEncrypted            = UsePassword && SecretStorageMode == SecretStorageMode.Save
            ? CredentialHelper.Encrypt(Password)
            : null,
        PrivateKeyPath               = UseKey      ? PrivateKeyPath                                  : null,
        PrivateKeyPassphraseEncrypted = UseKey && SecretStorageMode == SecretStorageMode.Save
            ? CredentialHelper.Encrypt(PrivateKeyPassphrase)
            : null,
        HostKeyFingerprint            = _hostKeyFingerprintCleared ? string.Empty : HostKeyFingerprint,
        SecretStorageMode             = SecretStorageMode,
    };

    public void ClearHostKeyFingerprint()
    {
        _hostKeyFingerprintCleared = true;
        HostKeyFingerprint = null;
    }

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

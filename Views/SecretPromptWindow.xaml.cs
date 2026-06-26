using System.Windows;
using DriveLink.Helpers;
using DriveLink.Models;

namespace DriveLink.Views;

public partial class SecretPromptWindow : Window
{
    private readonly ConnectionProfile _profile;

    public ConnectionProfile? ResultProfile { get; private set; }

    public string DisplayName { get; }
    public string HostDisplay { get; }
    public string RemotePath { get; }
    public string SecretLabel { get; }
    public string HelpText { get; }

    public SecretPromptWindow(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        DisplayName = profile.DisplayName;
        HostDisplay = $"{profile.Host} : {profile.Port}";
        RemotePath = profile.RemotePath;
        SecretLabel = profile.AuthMode == AuthMode.PrivateKey
            ? "KEY PASSPHRASE (OPTIONAL)"
            : "PASSWORD";
        HelpText = profile.AuthMode == AuthMode.PrivateKey
            ? "The passphrase is used for this connection attempt only and is not saved."
            : "The password is used for this connection attempt only and is not saved.";
        DataContext = this;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_profile.AuthMode == AuthMode.Password && string.IsNullOrEmpty(SecretBox.Password))
        {
            MessageBox.Show(
                "Password is required.",
                "Credentials Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ResultProfile = CloneForMount(_profile, SecretBox.Password);
        DialogResult = true;
    }

    private static ConnectionProfile CloneForMount(ConnectionProfile source, string secret)
    {
        var clone = new ConnectionProfile
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            PrivateKeyPath = source.PrivateKeyPath,
            HostKeyFingerprint = source.HostKeyFingerprint,
            SecretStorageMode = source.SecretStorageMode,
            RemotePath = source.RemotePath,
            PreferredDriveLetter = source.PreferredDriveLetter,
            VolumeLabel = source.VolumeLabel,
            AutoConnect = source.AutoConnect,
            ReadOnlyMount = source.ReadOnlyMount,
            CacheDurationSeconds = source.CacheDurationSeconds,
            ConnectionTimeoutSeconds = source.ConnectionTimeoutSeconds,
            OperationTimeoutSeconds = source.OperationTimeoutSeconds,
            HideDotFilesOverride = source.HideDotFilesOverride,
            IsSystem = source.IsSystem,
            AllowDriveLetterOverride = source.AllowDriveLetterOverride,
        };

        if (source.AuthMode == AuthMode.PrivateKey)
            clone.PrivateKeyPassphraseEncrypted = CredentialHelper.Encrypt(secret);
        else
            clone.PasswordEncrypted = CredentialHelper.Encrypt(secret);

        return clone;
    }
}

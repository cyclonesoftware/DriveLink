using System.Windows;
using DriveLink.Helpers;
using DriveLink.Models;
using DriveLink.Services;
using DriveLink.ViewModels;
using Microsoft.Win32;

namespace DriveLink.Views;

public partial class CredentialsWindow : Window
{
    private readonly ConnectionProfile _profile;
    private readonly CredentialsViewModel _vm;

    /// <summary>The saved credential — available after DialogResult = true.</summary>
    public UserCredential? Result { get; private set; }

    public CredentialsWindow(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile    = profile;
        _vm         = new CredentialsViewModel(profile);
        DataContext  = _vm;

        // Pre-populate PasswordBoxes (not data-bindable).
        PasswordBox.Password   = CredentialHelper.Decrypt(profile.PasswordEncrypted);
        PassphraseBox.Password = CredentialHelper.Decrypt(profile.PrivateKeyPassphraseEncrypted);
    }

    private void TransferPasswordFields()
    {
        _vm.Password             = PasswordBox.Password;
        _vm.PrivateKeyPassphrase = PassphraseBox.Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Transfer PasswordBox values to ViewModel before validation.
        TransferPasswordFields();
        if (string.IsNullOrWhiteSpace(_vm.Username))
        {
            MessageBox.Show("Username is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.UsePassword &&
            _vm.SecretStorageMode == SecretStorageMode.Save &&
            string.IsNullOrEmpty(_vm.Password))
        {
            MessageBox.Show("A password is required for password authentication.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.UseKey && string.IsNullOrWhiteSpace(_vm.PrivateKeyPath))
        {
            MessageBox.Show(
                "A private key file path is required for key authentication.\n" +
                "Use Browse… to select an existing key, or Generate New Key Pair… to create one.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result       = _vm.ToUserCredential();
        DialogResult = true;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TransferPasswordFields();

        if (sender is FrameworkElement element)
            element.IsEnabled = false;

        try
        {
            var result = await new ConnectionTestService().TestAsync(BuildTestProfile());
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.HostKeyFingerprint))
                _vm.HostKeyFingerprint = result.HostKeyFingerprint;

            new ConnectionDiagnosticsWindow(result) { Owner = this }.ShowDialog();
            OfferTrustReceivedHostKey(result);
        }
        finally
        {
            if (sender is FrameworkElement el)
                el.IsEnabled = true;
        }
    }

    private void ResetHostKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Reset the trusted SSH host key for this connection?\n\n" +
                "The next successful test or connection will trust the first key received.",
                "Reset Host Key",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _vm.ClearHostKeyFingerprint();
    }

    private void OfferTrustReceivedHostKey(ConnectionTestResult result)
    {
        if (!result.HasHostKeyMismatch ||
            string.IsNullOrWhiteSpace(result.ReceivedHostKeyFingerprint))
        {
            return;
        }

        if (MessageBox.Show(
                "The server presented a different SSH host key.\n\n" +
                $"Trusted:  {result.TrustedHostKeyFingerprint}\n" +
                $"Received: {result.ReceivedHostKeyFingerprint}\n\n" +
                "Only continue if you verified this fingerprint with the server administrator.\n\n" +
                "Trust the received key in this dialog?",
                "Host Key Changed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _vm.HostKeyFingerprint = result.ReceivedHostKeyFingerprint;
        }
    }

    private ConnectionProfile BuildTestProfile() => new()
    {
        Id = _profile.Id,
        DisplayName = _profile.DisplayName,
        Host = _profile.Host,
        Port = _profile.Port,
        Username = _vm.Username,
        PasswordEncrypted = _vm.UsePassword ? CredentialHelper.Encrypt(_vm.Password) : null,
        PrivateKeyPath = _vm.UseKey ? _vm.PrivateKeyPath : null,
        PrivateKeyPassphraseEncrypted = _vm.UseKey ? CredentialHelper.Encrypt(_vm.PrivateKeyPassphrase) : null,
        HostKeyFingerprint = _vm.HostKeyFingerprint,
        SecretStorageMode = _vm.SecretStorageMode,
        RemotePath = _profile.RemotePath,
        PreferredDriveLetter = _profile.PreferredDriveLetter,
        VolumeLabel = _profile.VolumeLabel,
        AutoConnect = _profile.AutoConnect,
        ReadOnlyMount = _profile.ReadOnlyMount,
        CacheDurationSeconds = _profile.CacheDurationSeconds,
        ConnectionTimeoutSeconds = _profile.ConnectionTimeoutSeconds,
        OperationTimeoutSeconds = _profile.OperationTimeoutSeconds,
        HideDotFilesOverride = _profile.HideDotFilesOverride,
        IsSystem = _profile.IsSystem,
        AllowDriveLetterOverride = _profile.AllowDriveLetterOverride,
    };

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title  = "Select Private Key",
            Filter = "Key files|*.pem;*.ppk;id_rsa;id_ed25519|All files|*.*"
        };
        if (dialog.ShowDialog() == true)
            _vm.PrivateKeyPath = dialog.FileName;
    }

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        var win = new GenerateKeyWindow { Owner = this };
        if (win.ShowDialog() == true)
            _vm.PrivateKeyPath = win.SavedKeyPath;
    }
}

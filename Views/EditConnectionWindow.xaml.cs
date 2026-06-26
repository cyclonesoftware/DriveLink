using System.Windows;
using DriveLink.Helpers;
using Microsoft.Win32;
using DriveLink.Services;
using DriveLink.ViewModels;

namespace DriveLink.Views;

public partial class EditConnectionWindow : Window
{
    private readonly EditConnectionViewModel _vm;

    public EditConnectionWindow(EditConnectionViewModel vm)
    {
        InitializeComponent();
        _vm         = vm;
        DataContext  = vm;

        // PasswordBox.Password cannot be data-bound (it is not a DependencyProperty).
        // Pre-populate the controls from the VM so editing an existing connection
        // keeps the current credentials when the user saves without re-entering them.
        PasswordBox.Password   = vm.Password;
        PassphraseBox.Password = vm.PrivateKeyPassphrase;
    }

    private void TransferPasswordFields()
    {
        _vm.Password             = PasswordBox.Password;
        _vm.PrivateKeyPassphrase = PassphraseBox.Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        TransferPasswordFields();
        if (string.IsNullOrWhiteSpace(_vm.DisplayName) || string.IsNullOrWhiteSpace(_vm.Host))
        {
            MessageBox.Show("Display Name and Host are required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.Username))
        {
            MessageBox.Show("Username is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.Port is < 1 or > 65535)
        {
            MessageBox.Show("Port must be between 1 and 65535.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.CacheDurationSeconds is < 0 or > 300)
        {
            MessageBox.Show("Cache duration must be between 0 and 300 seconds.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.ConnectionTimeoutSeconds is < 1 or > 300)
        {
            MessageBox.Show("Connect timeout must be between 1 and 300 seconds.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.OperationTimeoutSeconds is < 5 or > 600)
        {
            MessageBox.Show("Operation timeout must be between 5 and 600 seconds.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.UsePassword &&
            _vm.SecretStorageMode == Models.SecretStorageMode.Save &&
            string.IsNullOrEmpty(_vm.Password))
        {
            MessageBox.Show("A password is required for password authentication.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.UseKey && string.IsNullOrWhiteSpace(_vm.PrivateKeyPath))
        {
            MessageBox.Show("A private key file path is required for key authentication.\n" +
                            "Use Browse… to select an existing key, or Generate New Key Pair… to create one.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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

    private Models.ConnectionProfile BuildTestProfile()
    {
        var profile = _vm.ToProfile();
        if (_vm.UsePassword)
            profile.PasswordEncrypted = CredentialHelper.Encrypt(_vm.Password);
        if (_vm.UseKey)
            profile.PrivateKeyPassphraseEncrypted = CredentialHelper.Encrypt(_vm.PrivateKeyPassphrase);
        return profile;
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

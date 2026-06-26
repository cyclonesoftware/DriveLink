using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DriveLink.Services;
using DriveLink.ViewModels;

namespace DriveLink.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ConfigMergeService _config;

    public MainWindow(MainViewModel vm, ConfigMergeService config)
    {
        InitializeComponent();
        _vm         = vm;
        _config     = config;
        DataContext  = vm;

        vm.OpenImportDialog      += OnOpenImportDialog;
        vm.OpenConnectionDialog  += OnOpenConnectionDialog;
        vm.OpenDuplicateDialog   += OnOpenDuplicateDialog;
        vm.OpenCredentialsDialog += OnOpenCredentialsDialog;
        vm.RequestSecretPrompt   += OnRequestSecretPrompt;
        vm.OpenLogFolderRequested += OnOpenLogFolderRequested;
        vm.CopySupportDetailsRequested += OnCopySupportDetailsRequested;
        vm.MountError            += msg => MessageBox.Show(msg, _vm.AppName,
                                       MessageBoxButton.OK, MessageBoxImage.Warning);

        // Re-check for the SSHFS-Win Manager config whenever the window regains
        // focus, so the import button appears if it's installed after launch.
        Activated += (_, _) => _vm.RefreshCanImport();

        Closing += (_, e) =>
        {
            if (App.IsShuttingDown) return;   // allow WPF to close the window during shutdown
            e.Cancel = true;
            Hide();
        };
    }

    private void OnOpenImportDialog()
    {
        List<Models.ConnectionProfile> candidates;
        try
        {
            candidates = _vm.ReadImportCandidates();
        }
        catch (InvalidOperationException ex)
        {
            // Parse failure — abort the import and make no changes.
            MessageBox.Show(ex.Message, _vm.AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (candidates.Count == 0)
        {
            MessageBox.Show(
                "No connections were found in the SSHFS-Win Manager configuration.",
                _vm.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ImportConnectionsWindow(candidates) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var (imported, skipped) = _vm.ImportConnections(dialog.SelectedProfiles);

        var summary = $"Imported {imported}. Set credentials before connecting.";
        if (skipped > 0)
            summary += $"\n\nSkipped {skipped} already-existing connection(s).";

        MessageBox.Show(summary, _vm.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenConnectionDialog(Models.ConnectionProfile? existingProfile)
    {
        var editVm = existingProfile != null
            ? EditConnectionViewModel.FromProfile(existingProfile)
            : new EditConnectionViewModel();

        var dialog = new EditConnectionWindow(editVm) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.SaveProfile(editVm.ToProfile(existingProfile?.Id));
    }

    private void OnOpenDuplicateDialog(Models.ConnectionProfile duplicateProfile)
    {
        var editVm = EditConnectionViewModel.FromProfile(duplicateProfile);

        var dialog = new EditConnectionWindow(editVm) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.AddDuplicatedProfile(editVm.ToProfile(duplicateProfile.Id));
    }

    private void OnOpenCredentialsDialog(Models.ConnectionProfile profile)
    {
        var win = new CredentialsWindow(profile) { Owner = this };
        if (win.ShowDialog() == true)
            _vm.SaveSystemCredentials(win.Result!);
    }

    private Models.ConnectionProfile? OnRequestSecretPrompt(Models.ConnectionProfile profile)
    {
        var win = new SecretPromptWindow(profile) { Owner = this };
        return win.ShowDialog() == true ? win.ResultProfile : null;
    }

    private void OnOpenLogFolderRequested(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnCopySupportDetailsRequested(string details)
    {
        Clipboard.SetText(details);
        MessageBox.Show(
            "Support details were copied to the clipboard.\n\nThey include connection names, hostnames, usernames, and recent redacted log messages, but not passwords or passphrases.",
            _vm.AppName,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_vm, _config) { Owner = this }.ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow(_vm) { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.IsShuttingDown = true;
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Selects the connection card on right-click so sidebar reflects the item
    /// and context menu actions target the correct connection.
    /// </summary>
    private void ConnectionCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = sender as DependencyObject;
        while (current != null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
            item.IsSelected = true;
    }
}

using System.Windows;
using DriveLink.Services;
using DriveLink.ViewModels;

namespace DriveLink.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _mainVm;
    private readonly ConfigMergeService _config;
    private readonly SettingsViewModel _settingsVm;
    private readonly bool _autoRunSnapshot;
    private readonly bool _hideDotSnapshot;
    private readonly int _logRetentionSnapshot;

    public SettingsWindow(MainViewModel mainVm, ConfigMergeService config)
    {
        InitializeComponent();
        _mainVm = mainVm;
        _config = config;

        _settingsVm = new SettingsViewModel(mainVm, config);
        DataContext = _settingsVm;

        // Snapshot values that are written immediately so Cancel can restore them.
        _autoRunSnapshot = mainVm.AutoRun;
        _hideDotSnapshot = config.HideDotFiles;
        _logRetentionSnapshot = config.LogRetentionDays;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _mainVm.AutoRun = _autoRunSnapshot;
        _config.HideDotFiles = _hideDotSnapshot;
        _config.LogRetentionDays = _logRetentionSnapshot;
        LoggingService.RetentionDays = _logRetentionSnapshot;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}

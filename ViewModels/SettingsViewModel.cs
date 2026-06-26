using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DriveLink.Services;

namespace DriveLink.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _main;
    private readonly ConfigMergeService _config;
    private LogRetentionOption? _logRetentionDays;

    public SettingsViewModel(MainViewModel main, ConfigMergeService config)
    {
        _main = main;
        _config = config;

        // Initialize log retention from persisted value
        var days = _config.LogRetentionDays;
        _logRetentionDays = LogRetentionOptions.FirstOrDefault(o => o.Days == days)
            ?? LogRetentionOptions.First(o => o.Days == 7);
    }

    public bool AutoRun
    {
        get => _main.AutoRun;
        set
        {
            if (_main.AutoRun != value)
            {
                _main.AutoRun = value;
                OnPropertyChanged();
            }
        }
    }

    // Dot-file global default (user preference)
    public bool HideDotFilesByDefault
    {
        get => _config.HideDotFiles;
        set
        {
            if (_config.HideDotFiles != value)
            {
                _config.HideDotFiles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowDotFilesByDefault));
            }
        }
    }

    public bool ShowDotFilesByDefault
    {
        get => !_config.HideDotFiles;
        set
        {
            bool newHide = !value;
            if (_config.HideDotFiles != newHide)
            {
                _config.HideDotFiles = newHide;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HideDotFilesByDefault));
            }
        }
    }

    // Log retention
    public IReadOnlyList<LogRetentionOption> LogRetentionOptions { get; } = new[]
    {
        new LogRetentionOption(3, "3 days"),
        new LogRetentionOption(7, "7 days (default)"),
        new LogRetentionOption(14, "14 days"),
        new LogRetentionOption(30, "30 days"),
    };

    public LogRetentionOption LogRetentionDays
    {
        get => _logRetentionDays!;
        set
        {
            if (_logRetentionDays != value)
            {
                _logRetentionDays = value;
                _config.LogRetentionDays = value.Days;
                OnPropertyChanged();
            }
        }
    }

    public ICommand ViewLogsCommand => _main.ViewLogsCommand;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record LogRetentionOption(int Days, string Label)
{
    public override string ToString() => Label;
}

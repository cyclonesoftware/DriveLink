using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using DriveLink.Models;

namespace DriveLink.Views;

/// <summary>
/// Modal that lists connections found in the SSHFS-Win Manager config and lets
/// the user pick which to import. The window only collects the selection; the
/// caller performs the actual import (duplicate detection + persistence).
/// </summary>
public partial class ImportConnectionsWindow : Window
{
    public ObservableCollection<ImportItem> Items { get; }

    /// <summary>The connections the user chose to import (set when DialogResult is true).</summary>
    public IReadOnlyList<ConnectionProfile> SelectedProfiles { get; private set; }
        = Array.Empty<ConnectionProfile>();

    public ImportConnectionsWindow(IEnumerable<ConnectionProfile> candidates)
    {
        InitializeComponent();
        Items = new ObservableCollection<ImportItem>(
            candidates.Select(c => new ImportItem(c)));
        DataContext = this;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void DeselectAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var item in Items) item.IsSelected = selected;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SelectedProfiles = Items.Where(i => i.IsSelected).Select(i => i.Profile).ToList();
        DialogResult = true;
    }
}

/// <summary>A single importable connection row with a checkbox state.</summary>
public sealed class ImportItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public ImportItem(ConnectionProfile profile)
    {
        Profile = profile;
    }

    public ConnectionProfile Profile { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Profile.DisplayName)
        ? Profile.Host
        : Profile.DisplayName;

    public string Detail => $"{Profile.Host}:{Profile.Port}  ·  {Profile.RemotePath}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

using System.Windows;
using DriveLink.Services;

namespace DriveLink.Views;

public partial class ConnectionDiagnosticsWindow : Window
{
    private readonly ConnectionTestResult _result;

    public ConnectionDiagnosticsWindow(ConnectionTestResult result)
    {
        InitializeComponent();
        _result = result;
        DataContext = result;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_result.ToClipboardText());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

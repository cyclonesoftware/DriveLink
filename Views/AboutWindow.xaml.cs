using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using DriveLink.ViewModels;

namespace DriveLink.Views;

public partial class AboutWindow : Window
{
    // ── Placeholder URLs ─────────────────────────────────────────────────────
    // TODO: Replace SponsorUrl with your GitHub Sponsors page once set up.
    //       Replace WhiteLabelUrl with a mailto: or web link for enquiries.
    //       Both can be left as empty string to disable the corresponding link.
    private const string SponsorUrl    = Branding.SponsorUrl;
    private const string WhiteLabelUrl = Branding.WhiteLabelUrl;
    // ─────────────────────────────────────────────────────────────────────────

    public AboutWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Populate about text — fall back to a brief description if not configured.
        AboutTextBlock.Text = string.IsNullOrWhiteSpace(vm.AboutText)
            ? Branding.Description
            : vm.AboutText;

        // Show or hide the support URL row based on whether a URL is configured.
        bool hasUrl = !string.IsNullOrWhiteSpace(vm.SupportUrl)
                   && Uri.TryCreate(vm.SupportUrl, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        if (hasUrl)
        {
            Uri.TryCreate(vm.SupportUrl, UriKind.Absolute, out var validUri);
            SupportLinkText.Text    = vm.SupportUrl;
            SupportLink.NavigateUri = validUri!;
        }
        else
        {
            SupportRow.Visibility       = Visibility.Collapsed;
            SupportSeparator.Visibility = Visibility.Collapsed;
        }

        // ── Get Involved panel ───────────────────────────────────────────────
        // Hide the panel entirely when an organisation has applied custom branding
        // (AppName != "DriveLink") — they are already a paying/customised customer.
        if (!vm.IsDefaultBranding)
        {
            DefaultBrandingPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Wire sponsor link — disable if placeholder not yet replaced.
            if (Uri.TryCreate(SponsorUrl, UriKind.Absolute, out var sponsorUri)
                && !SponsorUrl.Contains("TODO"))
            {
                SponsorLink.NavigateUri = sponsorUri;
            }
            else
            {
                SponsorLink.IsEnabled = false;
            }

            // Wire white-label link — disable until URL is configured.
            if (!string.IsNullOrWhiteSpace(WhiteLabelUrl)
                && Uri.TryCreate(WhiteLabelUrl, UriKind.Absolute, out var whitelabelUri))
            {
                WhiteLabelLink.NavigateUri = whitelabelUri;
            }
            else
            {
                WhiteLabelLink.IsEnabled = false;
            }
        }
    }

    private void SupportLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void SponsorLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void WhiteLabelLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

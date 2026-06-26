namespace DriveLink.Models;

public class SystemConfig
{
    // Branding (AppName, AppIcon, AboutText, SupportUrl) is managed via the
    // Windows registry at HKLM\SOFTWARE\DriveLink — see BrandingService.
    public bool AllowUserDriveLetterOverride { get; set; } = true;
    public bool HideDotFiles { get; set; } = false;
    public List<ConnectionProfile> Connections { get; set; } = new();

    /// <summary>
    /// Optional HTTPS URL pointing to a remote system.json.
    /// When set (via registry or this field), the app downloads the remote file in the
    /// background and merges its connections on top of the local config.
    /// Bootstrap scenario: deploy a minimal system.json that only sets this field;
    /// the full connection list is then managed centrally via the URL.
    /// Priority: ADMX policy key > registry preference key > this field.
    /// </summary>
    public string? ConfigUrl { get; set; }
}

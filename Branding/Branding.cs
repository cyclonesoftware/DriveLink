namespace DriveLink;

/// <summary>
/// Single source of truth for all white-label branding constants.
///
/// To create a branded fork, change ONLY this file (and swap icon assets
/// in Resources/).  Every other file in the solution references these
/// constants instead of hard-coding the product name or URLs.
///
/// Runtime overrides (via registry / GPO) are layered on top of these
/// defaults by <see cref="Services.BrandingService"/>.
/// </summary>
public static class Branding
{
    // ── App identity ─────────────────────────────────────────────────
    public const string AppName           = "DriveLink";
    public const string Manufacturer      = "DriveLink";
    public const string Description       = "SFTP drive mounting for Windows via WinFsp and SSH.NET.";

    // ── URLs ─────────────────────────────────────────────────────────
    public const string SponsorUrl        = "https://github.com/cyclonesoftware/DriveLink";
    public const string WhiteLabelUrl     = "https://drivelink.cyclonesoftwaresolutions.com";
    public const string AppUrl            = "https://drivelink.cyclonesoftwaresolutions.com";

    // ── Registry paths (derived from AppName) ─────────────────────────
    public const string RegistryPolicies  = @"SOFTWARE\Policies\" + AppName;
    public const string RegistryPrefs     = @"SOFTWARE\" + AppName;

    // ── Filesystem / data folders (derived from AppName) ─────────────
    public const string DataFolderName    = AppName;
    public const string FileSystemName    = AppName;

    // ── About dialog — "Get Involved" panel ──────────────────────────
    // Shown only when AppName has not been overridden by a custom brand.
    public const string GetInvolvedText   = "Like " + AppName + "? Get Involved.";
    public const string SponsorLabel      = "Sponsor on GitHub";
    public const string SponsorDesc       = "Support ongoing development with a monthly contribution";
    public const string WhiteLabelLabel   = "Custom branded deployment";
    public const string WhiteLabelDesc    = "Get a branded installer and GPO templates for your organisation";
}

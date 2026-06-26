using Microsoft.Win32;

namespace DriveLink.Services;

/// <summary>
/// Reads app branding from the Windows registry.
///
/// Two registry sources are checked in priority order:
///
///   1. HKEY_LOCAL_MACHINE\SOFTWARE\Policies\DriveLink   ← ADMX-enforced Group Policy
///      Set via GPMC using the DriveLink.admx administrative template.
///      Values here override the preference key below.
///
///   2. HKEY_LOCAL_MACHINE\SOFTWARE\DriveLink            ← installer scaffold / preferences
///      Set by the MSI (scaffold defaults) and optionally overwritten by
///      GPO Registry Preferences, Intune OMA-URI, or PowerShell.
///
///   Values (all REG_SZ, all optional):
///     AppName    — Window title and About dialog heading (default: "DriveLink")
///     AppIcon    — Absolute path to a .ico file for the tray icon
///     AboutText  — Paragraph shown in the About dialog
///     SupportUrl — Clickable URL shown in the About dialog (http/https)
///
/// PowerShell example — preference key (no ADMX required, run as admin):
///   $k = "HKLM:\SOFTWARE\DriveLink"
///   New-Item $k -Force | Out-Null
///   Set-ItemProperty $k AppName    "Contoso Drive Mapper"
///   Set-ItemProperty $k AboutText  "SFTP drive mapping for Contoso Ltd."
///   Set-ItemProperty $k SupportUrl "https://helpdesk.contoso.com"
///
/// PowerShell example — policy key (same effect as ADMX, enforced):
///   $k = "HKLM:\SOFTWARE\Policies\DriveLink"
///   New-Item $k -Force | Out-Null
///   Set-ItemProperty $k AppName "Contoso Drive Mapper"
/// </summary>
public class BrandingService
{
    private const string PoliciesKey    = Branding.RegistryPolicies;
    private const string PreferencesKey = Branding.RegistryPrefs;

    public string  AppName    { get; private set; } = Branding.AppName;
    public string? AppIcon    { get; private set; }
    public string? AboutText  { get; private set; }
    public string? SupportUrl { get; private set; }

    public void Load()
    {
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(PoliciesKey);
            using var pref   = Registry.LocalMachine.OpenSubKey(PreferencesKey);

            AppName    = ReadString(policy, pref, "AppName")    ?? AppName;
            AppIcon    = ReadString(policy, pref, "AppIcon");
            AboutText  = ReadString(policy, pref, "AboutText");
            SupportUrl = ReadString(policy, pref, "SupportUrl");
        }
        catch
        {
            // Registry unavailable or access denied — keep built-in defaults.
        }
    }

    /// <summary>
    /// Returns the non-empty value from the policy key if present;
    /// otherwise returns the value from the preference key (may be null).
    /// </summary>
    private static string? ReadString(RegistryKey? policy, RegistryKey? pref, string name)
    {
        if (policy?.GetValue(name) is string p && p.Length > 0) return p;
        return pref?.GetValue(name) as string;
    }
}

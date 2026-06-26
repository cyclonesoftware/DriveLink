using System.IO;
using System.Net.Http;
using System.Text.Json;
using DriveLink.Models;
using Microsoft.Win32;

namespace DriveLink.Services;

/// <summary>
/// Reads system-level configuration from four sources and merges them:
///
///   1. %ProgramData%\DriveLink\system.json         — deployed by MSI / GPO file preference
///   2. HKLM\SOFTWARE\Policies\DriveLink\           — ADMX-enforced Group Policy overrides
///   3. HKLM\SOFTWARE\DriveLink\Connections\        — per-connection config via GPO Registry
///                                                    Preferences or Intune
///   4. Remote URL (ConfigUrl)                      — downloaded in background after startup;
///                                                    connections merge with local (URL wins)
///
/// Precedence for global settings (e.g. AllowUserDriveLetterOverride):
///   ADMX policy key (SOFTWARE\Policies\DriveLink) beats remote URL beats system.json.
///
/// Precedence for connections:
///   Registry connections (SOFTWARE\DriveLink\Connections\{id}) replace JSON/URL connections
///   with the same ID.  URL connections replace JSON connections with the same ID.
///
/// ADMX policy values written to SOFTWARE\Policies\DriveLink:
///   AllowUserDriveLetterOverride  REG_DWORD  1 = allow, 0 = deny
///   HideDotFiles                  REG_DWORD  1 = hide, 0 = show
///   ConfigUrl                     REG_SZ     https:// URL to remote system.json
///   (configure via GPMC using the DriveLink.admx administrative template)
///
/// Registry structure per connection (SOFTWARE\DriveLink\Connections\{id}):
///   DisplayName             REG_SZ    "Production Files"
///   Host                    REG_SZ    "prod.corp.example.com"
///   Port                    REG_DWORD  22
///   RemotePath              REG_SZ    "/"
///   PreferredDriveLetter    REG_SZ    ""  (empty = auto-assign)
///   VolumeLabel             REG_SZ    ""  (optional)
///   HostKeyFingerprint      REG_SZ    "SHA256:..."  (optional)
///   ReadOnlyMount           REG_DWORD  0
///   CacheDurationSeconds    REG_DWORD  15
///   ConnectionTimeoutSeconds REG_DWORD 15
///   OperationTimeoutSeconds REG_DWORD  30
///   HideDotFilesOverride    REG_DWORD  0/1 (optional; absent = system default)
///   AutoConnect             REG_DWORD  0
///   AllowDriveLetterOverride REG_DWORD 1
///
/// No credentials (passwords / keys) are ever stored in the registry.
/// </summary>
public class SystemConfigService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Branding.DataFolderName, "system.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented            = true,
        // system.json is hand-authored by IT admins using standard camelCase JSON convention.
        // CamelCase policy makes the serializer write "appName", "aboutText", etc. and
        // PropertyNameCaseInsensitive lets it read any casing (camelCase, PascalCase, mixed).
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive  = true,
    };

    // Shared, long-lived HttpClient — reused across all remote refresh calls.
    // 5-second timeout keeps the background fetch from stalling the UI.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public SystemConfig Config { get; private set; } = new();

    /// <summary>
    /// The resolved HTTPS URL to download a remote system.json from.
    /// Null when no ConfigUrl is configured.  Set by <see cref="Load"/>.
    /// Priority: ADMX policy key > registry preference key > system.json configUrl field.
    /// </summary>
    public string? RemoteConfigUrl { get; private set; }

    public void Load()
    {
        // ── Step 1: load from system.json ────────────────────────────────
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<SystemConfig>(json, JsonOptions) ?? new SystemConfig();
            }
            catch
            {
                Config = new SystemConfig();
            }
        }
        else
        {
            Config = new SystemConfig();
        }

        // ── Step 1b: apply ADMX-enforced policy overrides ────────────────
        // SOFTWARE\Policies\DriveLink is written by the DriveLink ADMX template
        // and takes precedence over system.json values.
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(Branding.RegistryPolicies);
            if (policyKey?.GetValue("AllowUserDriveLetterOverride") is int v)
                Config.AllowUserDriveLetterOverride = v != 0;
            if (policyKey?.GetValue("HideDotFiles") is int hdf)
                Config.HideDotFiles = hdf != 0;
        }
        catch
        {
            // Silent fail — policy key absent or access denied; keep JSON value.
        }

        // ── Step 1c: resolve ConfigUrl ────────────────────────────────────
        // Three sources in priority order (highest first):
        //   1. HKLM\SOFTWARE\Policies\DriveLink\ConfigUrl  (ADMX-enforced)
        //   2. HKLM\SOFTWARE\DriveLink\ConfigUrl           (registry preference)
        //   3. Config.ConfigUrl                            (system.json bootstrap field)
        RemoteConfigUrl = null;
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(Branding.RegistryPolicies);
            using var prefKey   = Registry.LocalMachine.OpenSubKey(Branding.RegistryPrefs);

            string? url = policyKey?.GetValue("ConfigUrl") as string
                       ?? prefKey?.GetValue("ConfigUrl")   as string
                       ?? Config.ConfigUrl;

            // Only accept https:// URLs — reject http:// and anything else silently.
            if (!string.IsNullOrWhiteSpace(url) &&
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                RemoteConfigUrl = url;
            }
        }
        catch
        {
            // Silent fail — registry access may be denied in some environments.
        }

        // ── Step 2: load from registry and merge ─────────────────────────
        // Registry connections replace JSON connections with the same ID.
        var registryConnections = LoadFromRegistry();
        if (registryConnections.Count > 0)
        {
            Config.Connections = Config.Connections
                .Where(j => !registryConnections.Any(r => r.Id == j.Id))
                .Concat(registryConnections)
                .ToList();
        }

        // ── Step 3: apply IsSystem + global AllowDriveLetterOverride ─────
        ApplySystemFlags();
    }

    /// <summary>
    /// Downloads the remote system.json from <see cref="RemoteConfigUrl"/> and merges
    /// its connections into <see cref="Config"/>.  Called in the background after the
    /// synchronous <see cref="Load"/> so the UI is never blocked.
    ///
    /// Merge rules:
    ///   • Remote connections whose ID matches a registry-sourced connection are ignored
    ///     (registry always wins).
    ///   • Remote connections replace local JSON connections with the same ID.
    ///   • Remote <see cref="SystemConfig.AllowUserDriveLetterOverride"/> is applied, then
    ///     the ADMX policy key is re-read to ensure it always takes final precedence.
    ///
    /// Returns <c>true</c> if the merge was applied; <c>false</c> when no URL is configured
    /// or any network/parse error occurs (original config is kept unchanged on failure).
    /// </summary>
    public async Task<bool> MergeRemoteAsync()
    {
        if (RemoteConfigUrl is null) return false;

        SystemConfig remote;
        try
        {
            var json = await _http.GetStringAsync(RemoteConfigUrl).ConfigureAwait(false);
            remote   = JsonSerializer.Deserialize<SystemConfig>(json, JsonOptions) ?? new SystemConfig();
        }
        catch
        {
            // Network error, timeout, bad JSON — keep local config, return silently.
            return false;
        }

        // Identify which connections came from the registry (they take highest priority
        // and must not be displaced by the remote config).
        var registryIds = new HashSet<string>(
            LoadFromRegistry().Select(c => c.Id),
            StringComparer.OrdinalIgnoreCase);

        // Keep local connections whose ID is NOT in the remote set (and not in registry).
        // Then append all remote connections that aren't overridden by the registry.
        var remoteIds = new HashSet<string>(
            remote.Connections.Select(c => c.Id),
            StringComparer.OrdinalIgnoreCase);

        Config.Connections = Config.Connections
            .Where(c => !remoteIds.Contains(c.Id) || registryIds.Contains(c.Id))
            .Concat(remote.Connections.Where(c => !registryIds.Contains(c.Id)))
            .ToList();

        // Apply remote global settings, then re-enforce ADMX so it always wins.
        Config.AllowUserDriveLetterOverride = remote.AllowUserDriveLetterOverride;
        Config.HideDotFiles = remote.HideDotFiles;
        try
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey(Branding.RegistryPolicies);
            if (policyKey?.GetValue("AllowUserDriveLetterOverride") is int v)
                Config.AllowUserDriveLetterOverride = v != 0;
            if (policyKey?.GetValue("HideDotFiles") is int hdf)
                Config.HideDotFiles = hdf != 0;
        }
        catch { }

        // Re-apply IsSystem flag and global drive-letter permission to all connections.
        ApplySystemFlags();

        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks every connection as system-owned and inherits the global
    /// AllowDriveLetterOverride flag where the per-connection flag is unset.
    /// </summary>
    private void ApplySystemFlags()
    {
        foreach (var c in Config.Connections)
        {
            c.IsSystem = true;
            // Inherit global override flag if not explicitly set per connection
            if (!c.AllowDriveLetterOverride && Config.AllowUserDriveLetterOverride)
                c.AllowDriveLetterOverride = true;
        }
    }

    /// <summary>
    /// Reads system connections from HKLM\SOFTWARE\DriveLink\Connections\.
    /// Each subkey is one connection; the subkey name is the connection ID.
    /// Returns an empty list (never throws) if the key is absent or unreadable.
    /// </summary>
    private List<ConnectionProfile> LoadFromRegistry()
    {
        var connections = new List<ConnectionProfile>();
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(Branding.RegistryPrefs + @"\Connections");
            if (baseKey is null) return connections;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                // Skip the ReadMe scaffold value that the MSI creates
                using var connKey = baseKey.OpenSubKey(subKeyName);
                if (connKey is null) continue;

                var conn = new ConnectionProfile
                {
                    Id          = subKeyName,
                    DisplayName = connKey.GetValue("DisplayName") as string ?? subKeyName,
                    Host        = connKey.GetValue("Host")        as string ?? "",
                    Port        = connKey.GetValue("Port") is int p ? p : 22,
                    RemotePath  = connKey.GetValue("RemotePath")  as string ?? "/",
                    VolumeLabel = connKey.GetValue("VolumeLabel") as string,
                    HostKeyFingerprint = connKey.GetValue("HostKeyFingerprint") as string,
                    ReadOnlyMount = connKey.GetValue("ReadOnlyMount") is int rom && rom != 0,
                    CacheDurationSeconds = connKey.GetValue("CacheDurationSeconds") is int cds ? cds : 15,
                    ConnectionTimeoutSeconds = connKey.GetValue("ConnectionTimeoutSeconds") is int cts ? cts : 15,
                    OperationTimeoutSeconds = connKey.GetValue("OperationTimeoutSeconds") is int ots ? ots : 30,
                    IsSystem    = true,
                };

                if (connKey.GetValue("HideDotFilesOverride") is int hdf)
                    conn.HideDotFilesOverride = hdf != 0;

                // PreferredDriveLetter — stored as single-char REG_SZ; empty = auto
                if (connKey.GetValue("PreferredDriveLetter") is string dl
                    && dl.Length == 1 && char.IsLetter(dl[0]))
                {
                    conn.PreferredDriveLetter = char.ToUpper(dl[0]);
                }

                // AutoConnect — REG_DWORD; 0 = false, non-zero = true
                if (connKey.GetValue("AutoConnect") is int ac)
                    conn.AutoConnect = ac != 0;

                // AllowDriveLetterOverride — REG_DWORD; global flag applied in Load() if absent
                if (connKey.GetValue("AllowDriveLetterOverride") is int alo)
                    conn.AllowDriveLetterOverride = alo != 0;

                // Require a non-empty Host — skip misconfigured entries silently
                if (!string.IsNullOrWhiteSpace(conn.Host))
                    connections.Add(conn);
            }
        }
        catch
        {
            // Silent fail — same pattern as BrandingService and JSON loading
        }

        return connections;
    }

    /// <summary>
    /// Writes a starter system.json to ProgramData for admins to customize.
    /// Only call this from a setup/installer context.
    /// Branding (AppName, AppIcon, etc.) is stored in the registry — see BrandingService.
    /// </summary>
    public static void WriteTemplate()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var template = new SystemConfig
        {
            AllowUserDriveLetterOverride = true,
            Connections =
            [
                new ConnectionProfile
                {
                    Id           = "example-server",
                    DisplayName  = "Example Server",
                    Host         = "server.corp.example.com",
                    Port         = 22,
                    Username     = "svc_sftp",
                    RemotePath   = "/",
                    IsSystem     = true,
                    AllowDriveLetterOverride = true,
                    AutoConnect  = false,
                }
            ]
        };

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(template, JsonOptions));
    }
}

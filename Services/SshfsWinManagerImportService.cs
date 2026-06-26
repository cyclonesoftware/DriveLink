using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DriveLink.Models;

namespace DriveLink.Services;

/// <summary>
/// Reads connection definitions exported by SSHFS-Win Manager
/// (<c>%AppData%\sshfs-win-manager\connections.json</c>) and maps them to
/// DriveLink <see cref="ConnectionProfile"/> objects for import.
///
/// Only non-secret fields are imported — SSHFS-Win Manager stores passwords in
/// plaintext or unencoded Base64, so they are never read or carried over. Imported
/// connections are user connections with <see cref="SecretStorageMode.AskAtConnect"/>,
/// so the user supplies credentials fresh the first time they connect.
/// </summary>
public class SshfsWinManagerImportService
{
    private static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "sshfs-win-manager", "connections.json");

    private readonly string _configPath;

    public SshfsWinManagerImportService(string? configPath = null)
    {
        _configPath = configPath ?? DefaultConfigPath;
    }

    /// <summary>Full path to the SSHFS-Win Manager connections file.</summary>
    public string ConfigPath => _configPath;

    /// <summary>True when an SSHFS-Win Manager connections file is present to import from.</summary>
    public bool ConfigExists() => File.Exists(_configPath);

    /// <summary>
    /// Reads and parses the SSHFS-Win Manager config, returning one
    /// <see cref="ConnectionProfile"/> per record (no secrets, all user-owned).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with a plain-English message when the file is missing or cannot be parsed.
    /// The caller should abort the import and make no changes.
    /// </exception>
    public List<ConnectionProfile> ReadConnections()
    {
        if (!ConfigExists())
        {
            throw new InvalidOperationException(
                "No SSHFS-Win Manager connections file was found at " +
                $"'{_configPath}'.");
        }

        List<SshfsWinRecord>? records;
        try
        {
            var json = File.ReadAllText(_configPath);
            records = JsonSerializer.Deserialize<List<SshfsWinRecord>>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The SSHFS-Win Manager connections file could not be read. " +
                "It may be corrupt or in an unexpected format, so nothing was imported.", ex);
        }

        if (records is null)
        {
            throw new InvalidOperationException(
                "The SSHFS-Win Manager connections file was empty or in an unexpected format, " +
                "so nothing was imported.");
        }

        return records
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Host))
            .Select(MapToProfile)
            .ToList();
    }

    private static ConnectionProfile MapToProfile(SshfsWinRecord r) => new()
    {
        // Non-secret fields only. Id is freshly generated; password/authType/privateKeyPath
        // from SSHFS-Win Manager are intentionally discarded.
        DisplayName = string.IsNullOrWhiteSpace(r.Name) ? r.Host! : r.Name!,
        Host        = r.Host!,
        Port        = r.Port is > 0 and <= 65535 ? r.Port : 22,
        Username    = r.Username ?? string.Empty,
        RemotePath  = string.IsNullOrWhiteSpace(r.Path) ? "/" : r.Path!,
        PreferredDriveLetter = ParseMountPoint(r.MountPoint),

        // Credentials are supplied fresh by the user; never auto-connect a connection
        // that has no stored secret.
        SecretStorageMode = SecretStorageMode.AskAtConnect,
        AutoConnect       = false,
        IsSystem          = false,
    };

    /// <summary>Maps an SSHFS-Win Manager mount point (e.g. "Z" or "Z:") to a drive letter.</summary>
    private static char? ParseMountPoint(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint)) return null;
        var c = char.ToUpperInvariant(mountPoint.Trim()[0]);
        return c is >= 'A' and <= 'Z' ? c : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Subset of the SSHFS-Win Manager record we care about. Secret-bearing fields
    /// (password, authType, privateKeyPath) and the source id are deliberately omitted.
    /// </summary>
    private sealed class SshfsWinRecord
    {
        [JsonPropertyName("name")]       public string? Name       { get; set; }
        [JsonPropertyName("host")]       public string? Host       { get; set; }
        [JsonPropertyName("port")]       public int     Port       { get; set; }
        [JsonPropertyName("path")]       public string? Path       { get; set; }
        [JsonPropertyName("username")]   public string? Username   { get; set; }
        [JsonPropertyName("mountPoint")] public string? MountPoint { get; set; }
    }
}

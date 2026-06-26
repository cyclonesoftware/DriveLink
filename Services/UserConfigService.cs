using System.IO;
using System.Text.Json;
using DriveLink.Models;

namespace DriveLink.Services;

/// <summary>
/// Reads and writes user-level config from %AppData%\DriveLink\user.json.
/// Stores user-added connections and drive letter overrides for system connections.
/// </summary>
public class UserConfigService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Branding.DataFolderName, "user.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented            = true,
        // Keep user.json in the same camelCase convention as system.json.
        // Case-insensitive reading ensures existing PascalCase user.json files
        // continue to load correctly after this change.
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive  = true,
    };

    public UserConfig Config { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(ConfigPath)) { Config = new UserConfig(); return; }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            Config   = JsonSerializer.Deserialize<UserConfig>(json, JsonOptions) ?? new UserConfig();
        }
        catch { Config = new UserConfig(); }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
    }

    public void AddConnection(ConnectionProfile profile)
    {
        Config.Connections.Add(profile);
        Save();
    }

    public void UpdateConnection(ConnectionProfile profile)
    {
        var idx = Config.Connections.FindIndex(c => c.Id == profile.Id);
        if (idx >= 0) Config.Connections[idx] = profile;
        Save();
    }

    public void RemoveConnection(string id)
    {
        Config.Connections.RemoveAll(c => c.Id == id);
        Save();
    }

    public void SetConnectionHostKeyFingerprint(string profileId, string fingerprint)
    {
        var conn = Config.Connections.FirstOrDefault(c => c.Id == profileId);
        if (conn == null) return;
        conn.HostKeyFingerprint = fingerprint;
        Save();
    }

    public void SetDriveLetterOverride(string profileId, char letter)
    {
        Config.DriveLetterOverrides[profileId] = letter;
        Save();
    }

    public void ClearDriveLetterOverride(string profileId)
    {
        Config.DriveLetterOverrides.Remove(profileId);
        Save();
    }

    /// <summary>
    /// Stores an auto-connect override for a system connection and persists to user.json.
    /// </summary>
    public void SetAutoConnectOverride(string profileId, bool value)
    {
        Config.AutoConnectOverrides[profileId] = value;
        Save();
    }

    /// <summary>
    /// Upserts the user's credentials for a system connection and persists to user.json.
    /// </summary>
    public void SaveCredential(UserCredential cred)
    {
        var idx = Config.Credentials.FindIndex(c => c.ProfileId == cred.ProfileId);
        if (idx >= 0)
        {
            Config.Credentials[idx] = cred;
        }
        else
        {
            Config.Credentials.Add(cred);
        }
        Save();
    }

    public void SetCredentialHostKeyFingerprint(string profileId, string? fingerprint)
    {
        var idx = Config.Credentials.FindIndex(c => c.ProfileId == profileId);
        if (idx >= 0)
        {
            Config.Credentials[idx].HostKeyFingerprint = fingerprint;
        }
        else if (fingerprint != null)
        {
            Config.Credentials.Add(new UserCredential
            {
                ProfileId = profileId,
                HostKeyFingerprint = fingerprint,
            });
        }
        Save();
    }
}

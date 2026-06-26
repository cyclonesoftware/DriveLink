using System.Text.Json.Serialization;

namespace DriveLink.Models;

public class ConnectionProfile
{
    public string  Id            { get; set; } = Guid.NewGuid().ToString();
    public string  DisplayName   { get; set; } = string.Empty;
    public string  Host          { get; set; } = string.Empty;
    public int     Port          { get; set; } = 22;
    public string  Username      { get; set; } = string.Empty;

    // Auth
    public string? PasswordEncrypted               { get; set; }
    public string? PrivateKeyPath                  { get; set; }
    public string? PrivateKeyPassphraseEncrypted   { get; set; }
    public string? HostKeyFingerprint              { get; set; }
    public SecretStorageMode SecretStorageMode     { get; set; } = SecretStorageMode.Save;

    public string  RemotePath    { get; set; } = "/";
    public char?   PreferredDriveLetter { get; set; }
    public string? VolumeLabel   { get; set; }
    public bool    AutoConnect   { get; set; } = false;

    // Advanced mount behavior. Defaults preserve the current DriveLink experience.
    public bool    ReadOnlyMount             { get; set; } = false;
    public int     CacheDurationSeconds      { get; set; } = 15;
    public int     ConnectionTimeoutSeconds  { get; set; } = 15;
    public int     OperationTimeoutSeconds   { get; set; } = 30;
    public bool?   HideDotFilesOverride      { get; set; }

    // System config only
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSystem { get; set; } = false;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowDriveLetterOverride { get; set; } = true;

    [JsonIgnore]
    public AuthMode AuthMode =>
        string.IsNullOrEmpty(PrivateKeyPath) ? AuthMode.Password : AuthMode.PrivateKey;

    [JsonIgnore]
    public bool RequiresSecretPrompt =>
        SecretStorageMode == SecretStorageMode.AskAtConnect;
}

public enum AuthMode { Password, PrivateKey }

public enum SecretStorageMode { Save, AskAtConnect }

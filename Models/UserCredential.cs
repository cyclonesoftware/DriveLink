namespace DriveLink.Models;

/// <summary>
/// Per-user credentials for a system-defined connection.
/// Stored in user.json, keyed by the system connection's <see cref="ConnectionProfile.Id"/>.
/// IT admins define the server (host, port, path) in system.json; each user supplies
/// their own username and SSH key or password here.
/// </summary>
public class UserCredential
{
    public string  ProfileId                     { get; set; } = string.Empty;
    public string? Username                      { get; set; }
    public string? PasswordEncrypted             { get; set; }
    public string? PrivateKeyPath                { get; set; }
    public string? PrivateKeyPassphraseEncrypted { get; set; }
    public string? HostKeyFingerprint            { get; set; }
    public SecretStorageMode SecretStorageMode   { get; set; } = SecretStorageMode.Save;
}

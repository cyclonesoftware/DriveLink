namespace DriveLink.Models;

public class UserConfig
{
    // False until the user has seen the main window at least once.
    // Used to show the window on first run even when system connections are pre-configured.
    public bool HasLaunched { get; set; } = false;

    public List<ConnectionProfile> Connections { get; set; } = new();

    // User overrides for system connection drive letters: profileId -> preferred letter
    public Dictionary<string, char> DriveLetterOverrides { get; set; } = new();

    // User overrides for the auto-connect flag on system connections: profileId -> bool
    public Dictionary<string, bool> AutoConnectOverrides { get; set; } = new();

    // Per-user credentials for system-defined connections (username + key/password).
    // IT defines the server details in system.json; each user stores their own auth here.
    public List<UserCredential> Credentials { get; set; } = new();

    // User preference for default dot-file visibility (true = hide .files by default).
    // Per-connection overrides still take precedence. Falls back to SystemConfig if not meaningful here?
    public bool HideDotFiles { get; set; } = false;

    // User preference for log retention in days.
    public int LogRetentionDays { get; set; } = 7;
}

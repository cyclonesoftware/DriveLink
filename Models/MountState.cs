namespace DriveLink.Models;

public class MountState
{
    public string ProfileId   { get; init; } = string.Empty;
    public char   DriveLetter { get; init; }
    public bool   IsMounted   { get; set; }

    /// <summary>
    /// True when the underlying SSH session dropped (network loss, VPN disconnect,
    /// server reboot) while the drive was mounted. The WinFsp host and drive letter
    /// are kept reserved so the user can recover with one click (Reconnect); the
    /// drive is not usable until then. Mutually exclusive with <see cref="IsMounted"/>.
    /// </summary>
    public bool   IsConnectionLost { get; set; }

    public string? ErrorMessage { get; set; }
}

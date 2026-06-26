using System.Collections.Concurrent;
using System.IO;
using Fsp;
using Renci.SshNet.Common;
using DriveLink.Helpers;
using DriveLink.Models;

namespace DriveLink.Services;

public class MountService
{
    private readonly ConfigMergeService _config;
    private readonly ConcurrentDictionary<string, ActiveMount> _mounts = new();

    public event EventHandler<MountStateChangedEventArgs>? MountStateChanged;

    public MountService(ConfigMergeService config) => _config = config;

    public bool IsMounted(string profileId) =>
        _mounts.TryGetValue(profileId, out var m) && m.State.IsMounted;

    public MountState? GetState(string profileId) =>
        _mounts.TryGetValue(profileId, out var m) ? m.State : null;

    public async Task MountAsync(ConnectionProfile profile)
    {
        if (IsMounted(profile.Id)) return;

        var driveLetter = ResolveDriveLetter(profile);
        var state       = new MountState { ProfileId = profile.Id, DriveLetter = driveLetter };
        LoggingService.Info("MountStarted", $"Mounting as {driveLetter}:.", profile);

        try
        {
            await Task.Run(() =>
            {
                bool systemHide = _config.SystemConfig.HideDotFiles;
                bool defaultHide = systemHide ? true : _config.HideDotFiles;
                var hideDotFiles = profile.HideDotFilesOverride ?? defaultHide;
                var cacheTimeout = Math.Clamp(profile.CacheDurationSeconds, 0, 300) * 1000;
                var fs   = new SftpFileSystem(profile, hideDotFiles);
                fs.ConnectionLost += () => OnConnectionLost(profile.Id, fs);
                var host = new FileSystemHost(fs)
                {
                    FileSystemName  = Branding.FileSystemName,

                    // ── Kernel-level cache timeouts ─────────────────────────────
                    // These tell the WinFsp KERNEL DRIVER to cache results so that
                    // repeated Explorer probes never reach our usermode code at all.
                    // This is equivalent to SSHFS-Win's -okernel_cache flag.
                    //
                    // Without ALL of these set, WinFsp calls back into our code for
                    // every GetSecurityByName, ReadDirectoryEntry, and GetVolumeInfo
                    // — even if the data hasn't changed — causing hundreds of
                    // unnecessary SSH round-trips per folder browse.

                    FileInfoTimeout   = (uint)cacheTimeout,  // file attribute cache (GetFileInfo)
                    DirInfoTimeout    = (uint)cacheTimeout,  // directory listing cache (ReadDirectoryEntry)
                    SecurityTimeout   = (uint)cacheTimeout,  // security descriptor cache (GetSecurityByName)
                    VolumeInfoTimeout = (uint)Math.Max(cacheTimeout, 30_000),  // volume info cache (GetVolumeInfo)

                    SectorSize               = 4096,
                    SectorsPerAllocationUnit = 1,
                };

                try
                {
                    fs.Connect();
                }
                catch (SshAuthenticationException ex)
                {
                    throw new InvalidOperationException(
                        $"Authentication failed for '{profile.Username}@{profile.Host}'.\n\n" +
                        $"SSH server replied: {ex.Message}\n\n" +
                        "Check that the username and password (or private key) stored in the " +
                        "connection settings are correct, then try again.", ex);
                }
                catch (SshConnectionException ex)
                {
                    throw new InvalidOperationException(
                        $"Could not reach '{profile.Host}:{profile.Port}'.\n\n" +
                        $"SSH server replied: {ex.Message}\n\n" +
                        "Verify the host address and port are correct and that the server is reachable.",
                        ex);
                }

                // Use MountEx with explicit thread count and Synchronized=false.
                // - ThreadCount=0: let WinFsp use the number of CPU cores
                // - Synchronized=false: allow concurrent filesystem operations
                //   (our code is thread-safe via per-handle locks and ConcurrentDictionary caches)
                // Without this, WinFsp may serialize ALL operations through a single
                // dispatcher thread — the #1 reason for sluggish browsing vs SSHFS-Win.
                int result = host.MountEx($"{driveLetter}:",
                    ThreadCount: 0, SecurityDescriptor: null, Synchronized: false, DebugLog: 0);
                if (result != 0)
                {
                    fs.Disconnect();
                    LoggingService.Error("WinFspMountFailed", $"WinFsp mount failed: 0x{result:X8}.", profile: profile);
                    throw new InvalidOperationException($"WinFsp mount failed: 0x{result:X8}");
                }

                state.IsMounted = true;
                _mounts[profile.Id] = new ActiveMount(fs, host, state);
            });

            _config.SaveHostKeyFingerprint(profile, profile.HostKeyFingerprint);
            LoggingService.Info("MountSucceeded", $"Mounted as {driveLetter}:.", profile);
            RaiseStateChanged(state);
        }
        catch (TypeInitializationException ex) when (ex.TypeName == "Fsp.Interop.Api")
        {
            var friendly = new InvalidOperationException(
                "WinFsp is not installed or could not be loaded.\n\n" +
                "DriveLink requires WinFsp to mount drives. " +
                "Download and install WinFsp from winfsp.dev, then try again.", ex);
            state.ErrorMessage = friendly.Message;
            LoggingService.Error("WinFspNotInstalled", "WinFsp is not installed or could not be loaded.", ex, profile);
            RaiseStateChanged(state);
            throw friendly;
        }
        catch (Exception ex)
        {
            state.ErrorMessage = ex.Message;
            LoggingService.Error("MountFailed", "Mount failed.", ex, profile);
            RaiseStateChanged(state);
            throw;
        }
    }

    public async Task UnmountAsync(string profileId)
    {
        if (!_mounts.TryRemove(profileId, out var mount)) return;
        LoggingService.Info("UnmountStarted", $"Unmounting {mount.State.DriveLetter}:.");

        await Task.Run(() =>
        {
            mount.Host.Unmount();
            mount.Host.Dispose();
            mount.Fs.Disconnect();
        }).ConfigureAwait(false);   // avoid deadlock when called via .GetAwaiter().GetResult()

        mount.State.IsMounted = false;
        mount.State.IsConnectionLost = false;
        LoggingService.Info("UnmountSucceeded", $"Unmounted {mount.State.DriveLetter}:.");
        RaiseStateChanged(mount.State);
    }

    /// <summary>
    /// Invoked when a mounted session's SSH connection drops. Transitions the mount
    /// into the "connection lost" state while keeping the WinFsp host and drive letter
    /// reserved, so the user can recover with one click (Reconnect) — see
    /// <see cref="MountState.IsConnectionLost"/>. No automatic reconnect is attempted.
    /// </summary>
    private void OnConnectionLost(string profileId, SftpFileSystem fs)
    {
        if (!_mounts.TryGetValue(profileId, out var mount)) return;
        // Ignore a late event from a session that has already been replaced
        // (e.g. after the user reconnected) or torn down.
        if (!ReferenceEquals(mount.Fs, fs)) return;
        if (mount.State.IsConnectionLost) return;

        mount.State.IsMounted = false;
        mount.State.IsConnectionLost = true;
        LoggingService.Warn("ConnectionLost",
            $"Connection lost on {mount.State.DriveLetter}:; awaiting user reconnect.");
        RaiseStateChanged(mount.State);
    }

    public void UnmountAll()
    {
        foreach (var id in _mounts.Keys.ToList())
            UnmountAsync(id).GetAwaiter().GetResult();
    }

    public async Task AutoConnectAsync()
    {
        foreach (var profile in _config.MergedConnections
                     .Where(p => p.AutoConnect)
                     .ToList())
        {
            if (profile.RequiresSecretPrompt)
            {
                LoggingService.Info("AutoConnectSkipped",
                    "Auto-connect skipped because this connection asks for a secret at connect time.",
                    profile);
                continue;
            }

            try { await MountAsync(profile); }
            catch (Exception ex)
            {
                LoggingService.Error("AutoConnectFailed", "Auto-connect failed.", ex, profile);
            }
        }
    }

    private char ResolveDriveLetter(ConnectionProfile profile)
    {
        if (profile.PreferredDriveLetter.HasValue)
        {
            var inUse = DriveInfo.GetDrives().Select(d => char.ToUpper(d.Name[0])).ToHashSet();
            if (!inUse.Contains(char.ToUpper(profile.PreferredDriveLetter.Value)))
                return char.ToUpper(profile.PreferredDriveLetter.Value);
        }
        var reserved = _mounts.Values.Select(m => m.State.DriveLetter);
        var next = DriveLetterHelper.GetNextAvailable(reserved);
        if (!next.HasValue)
            throw new InvalidOperationException(
                "No drive letters are available. Disconnect another mapped drive, then try again.");
        return next.Value;
    }

    private void RaiseStateChanged(MountState state) =>
        MountStateChanged?.Invoke(this, new MountStateChangedEventArgs(state));

    private sealed record ActiveMount(SftpFileSystem Fs, FileSystemHost Host, MountState State);
}

public class MountStateChangedEventArgs : EventArgs
{
    public MountState State { get; }
    public MountStateChangedEventArgs(MountState state) => State = state;
}

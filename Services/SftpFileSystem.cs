using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Fsp;
using Fsp.Interop;
using FileInfo = Fsp.Interop.FileInfo;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using DriveLink.Helpers;
using DriveLink.Models;

namespace DriveLink.Services;

public class SftpFileSystem : FileSystemBase
{
    private SftpClientPool _pool = null!;
    private readonly ConnectionProfile _profile;
    private readonly bool _hideDotFiles;
    private readonly bool _readOnly;
    private readonly TimeSpan _attrCacheTtl;
    private readonly TimeSpan _dirCacheTtl;
    private readonly TimeSpan _negCacheTtl;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _operationTimeout;
    private volatile string? _hostKeyMismatchMessage;

    // ── Dropped-connection detection ───────────────────────────────────────────
    //
    // A dedicated probe client (separate from the I/O pool, which transparently
    // re-creates dead clients and would mask a drop) plus a timer that performs a
    // fast-failing round-trip. Plain SSH keepalive doesn't reliably catch a silent
    // network drop (e.g. VPN death with no TCP RST), so we actively probe.
    private SftpClient? _monitor;
    private System.Threading.Timer? _healthTimer;
    private int _connectionLostSignaled;
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Raised once (on a background thread) when the underlying SSH session is
    /// detected to have dropped while mounted. Subscribers should not block.
    /// </summary>
    public event Action? ConnectionLost;

    // ── Caches ───────────────────────────────────────────────────────────────
    //
    // Attribute cache — avoids a GetAttributes round-trip on every kernel
    // metadata query (GetSecurityByName fires for every path lookup).
    private readonly ConcurrentDictionary<string, (SftpFileAttributes Attrs, DateTime Expires)>
        _attrCache = new(StringComparer.Ordinal);

    // Directory listing cache — avoids re-fetching when navigating back to a
    // folder or when Explorer refreshes a view.  Each entry holds the full
    // pre-built list so ReadDirectoryEntry just iterates in-memory.
    private readonly ConcurrentDictionary<string, (List<(string Name, FileInfo Info)> Entries, DateTime Expires)>
        _dirCache = new(StringComparer.Ordinal);

    // Negative lookup cache — remembers paths that returned "not found" so
    // repeated GetSecurityByName probes (Explorer tries .lnk, desktop.ini,
    // thumbs.db, etc.) don't each trigger a network round-trip.
    private readonly ConcurrentDictionary<string, DateTime>
        _negCache = new(StringComparer.Ordinal);

    public SftpFileSystem(ConnectionProfile profile, bool hideDotFiles = false)
    {
        _profile = profile;
        _hideDotFiles = hideDotFiles;
        _readOnly = profile.ReadOnlyMount;

        var cacheSeconds = Math.Clamp(profile.CacheDurationSeconds, 0, 300);
        _attrCacheTtl = TimeSpan.FromSeconds(cacheSeconds);
        _dirCacheTtl = TimeSpan.FromSeconds(cacheSeconds);
        _negCacheTtl = TimeSpan.FromSeconds(Math.Min(cacheSeconds, 10));
        _connectionTimeout = TimeSpan.FromSeconds(Math.Clamp(profile.ConnectionTimeoutSeconds, 1, 300));
        _operationTimeout = TimeSpan.FromSeconds(Math.Clamp(profile.OperationTimeoutSeconds, 5, 600));
    }

    // ── Per-handle context ────────────────────────────────────────────────────
    //
    // Stored in the WinFsp fileDesc slot.  Each FileContext borrows a dedicated
    // SFTP client from the pool for the lifetime of the open handle so that
    // file I/O doesn't contend with metadata operations on other channels.
    //
    // The SFTP stream is still opened lazily:
    //   • Open()   → allocates FileContext with pool lease, NO SSH_FXP_OPEN yet
    //   • Read()   → opens stream on first actual read, then reuses it
    //   • Write()  → same
    //   • Close()  → disposes stream, returns client to pool

    private sealed class FileContext : IDisposable
    {
        private readonly SftpClientPool _pool;
        private SftpClientPool.Lease? _lease;
        private readonly string _path;
        private SftpFileStream? _stream;

        public readonly object Lock = new();

        private bool _disposed;

        // ── Read-ahead state ────────────────────────────────────────────
        private const int ReadAheadSize = 256 * 1024; // 256 KiB (matches SftpClient.BufferSize)
        private long _lastReadEnd = -1;
        private long _prefetchOffset = -1;
        private byte[]? _prefetchBuf;
        private int _prefetchLen;
        private Task? _prefetchTask;

        /// <summary>
        /// Lazy constructor — does NOT acquire a pool connection.  The vast
        /// majority of FileContexts opened by Explorer are for metadata only
        /// and never read/write, so deferring the connection avoids exhausting
        /// the pool during directory browsing and bulk copy operations.
        /// </summary>
        public FileContext(SftpClientPool pool, string path)
        {
            _pool = pool;
            _path = path;
        }

        /// <summary>
        /// Eager constructor for Create — acquires a pool connection immediately
        /// because we know data will be written right away.
        /// </summary>
        public FileContext(SftpClientPool pool, string path, Func<SftpClient, SftpFileStream> openStream)
        {
            _pool   = pool;
            _lease  = pool.Acquire();
            _path   = path;
            _stream = openStream(_lease.Value.Client);
        }

        public SftpFileStream GetOrOpenStreamUnsafe()
        {
            if (_stream != null) return _stream;
            // First actual I/O — acquire a pool connection now.
            _lease ??= _pool.Acquire();
            try   { _stream = _lease.Value.Client.Open(_path, FileMode.Open, FileAccess.ReadWrite); }
            catch { _stream = _lease.Value.Client.Open(_path, FileMode.Open, FileAccess.Read); }
            return _stream;
        }

        /// <summary>
        /// Attempts to satisfy a read from the prefetch buffer.
        /// Returns the number of bytes copied, or 0 if the prefetch
        /// doesn't cover the requested range.  Must be called under Lock.
        /// </summary>
        public int TryReadFromPrefetch(long offset, byte[] dest, int destOffset, int count)
        {
            if (_prefetchTask == null) return 0;

            // Wait for the in-flight prefetch to complete.
            try { _prefetchTask.Wait(); } catch { return 0; }

            if (_prefetchBuf == null || _prefetchLen == 0) return 0;
            if (offset != _prefetchOffset) return 0;

            int toCopy = Math.Min(count, _prefetchLen);
            Buffer.BlockCopy(_prefetchBuf, 0, dest, destOffset, toCopy);

            // Consume the prefetch.
            _prefetchOffset = -1;
            _prefetchLen    = 0;
            _prefetchTask   = null;
            return toCopy;
        }

        /// <summary>
        /// After a successful read at (offset, bytesRead), kick off an async
        /// prefetch of the next block if the access pattern is sequential.
        /// Must be called under Lock.
        ///
        /// The prefetch task does NOT acquire Lock — it relies on the natural
        /// ordering: the caller holds Lock while starting the task, releases
        /// Lock when Read returns, the task runs on the thread pool and
        /// accesses the stream exclusively, then the next Read waits for the
        /// task via TryReadFromPrefetch before touching the stream again.
        /// </summary>
        public void MaybeStartPrefetch(long offset, int bytesRead)
        {
            long readEnd = offset + bytesRead;
            bool sequential = (_lastReadEnd == offset) || (_lastReadEnd == -1);
            _lastReadEnd = readEnd;

            if (!sequential || _disposed) return;

            // Don't start another prefetch if one is already in flight for this offset.
            if (_prefetchTask != null && _prefetchOffset == readEnd) return;

            var stream = _stream;
            if (stream == null) return;

            _prefetchOffset = readEnd;
            _prefetchBuf ??= new byte[ReadAheadSize];
            _prefetchTask = Task.Run(() =>
            {
                try
                {
                    if (_disposed) return;
                    stream.Seek(readEnd, SeekOrigin.Begin);
                    _prefetchLen = stream.Read(_prefetchBuf, 0, ReadAheadSize);
                }
                catch { _prefetchLen = 0; }
            });
        }

        /// <summary>Resets read-ahead tracking (e.g. after a write).</summary>
        public void ResetReadAhead()
        {
            _lastReadEnd    = -1;
            _prefetchOffset = -1;
            _prefetchLen    = 0;
            _prefetchTask   = null;
        }

        public void Dispose()
        {
            lock (Lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _prefetchTask?.Wait(500); } catch { }
                try { _stream?.Flush(); } catch { }
                _stream?.Dispose();
                _stream = null;
                _lease?.Dispose(); // only returns to pool if we ever acquired
            }
        }
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public void Connect()
    {
        // Create a pool of SFTP connections (up to 4 concurrent channels).
        // The first connection is created eagerly to fail-fast on auth errors;
        // the rest are created on demand as WinFsp issues concurrent requests.
        _pool = new SftpClientPool(() =>
        {
            var connInfo = new ConnectionInfo(_profile.Host, _profile.Port,
                _profile.Username, BuildAuthMethods());
            connInfo.Timeout = _connectionTimeout;
            var client = new SftpClient(connInfo)
            {
                OperationTimeout = _operationTimeout,
            };
            client.HostKeyReceived += VerifyHostKey;
            return client;
        }, poolSize: 4);

        // Validate connectivity by acquiring and immediately returning one client.
        try
        {
            using var warmup = _pool.Acquire();
        }
        catch (SshConnectionException ex) when (!string.IsNullOrWhiteSpace(_hostKeyMismatchMessage))
        {
            throw new InvalidOperationException(_hostKeyMismatchMessage, ex);
        }

        StartConnectionMonitor();
    }

    /// <summary>
    /// Brings up the dedicated probe client and health timer used to detect a
    /// dropped session. Failure to start monitoring is non-fatal — the mount still
    /// works, it just won't surface a "connection lost" state proactively.
    /// </summary>
    private void StartConnectionMonitor()
    {
        try
        {
            var connInfo = new ConnectionInfo(_profile.Host, _profile.Port,
                _profile.Username, BuildAuthMethods());
            connInfo.Timeout = _connectionTimeout;
            var monitor = new SftpClient(connInfo)
            {
                OperationTimeout  = _operationTimeout,
                KeepAliveInterval = TimeSpan.FromSeconds(15),
            };
            monitor.HostKeyReceived += VerifyHostKey;
            monitor.ErrorOccurred   += (_, _) => SignalConnectionLost();
            monitor.Connect();
            _monitor = monitor;

            _healthTimer = new System.Threading.Timer(
                _ => HealthCheck(), null, HealthCheckInterval, HealthCheckInterval);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ConnectionMonitorStartFailed",
                "Could not start the dropped-connection monitor; the mount will still work.",
                _profile);
            _ = ex;
        }
    }

    /// <summary>
    /// Probes the live session with a cheap round-trip. A network/SSH failure that
    /// leaves the probe disconnected is treated as a dropped connection.
    /// </summary>
    private void HealthCheck()
    {
        var monitor = _monitor;
        if (monitor is null || _connectionLostSignaled == 1) return;

        try
        {
            monitor.GetAttributes(_profile.RemotePath);
        }
        catch
        {
            // Only a genuinely-down session counts as "lost". A transient operation
            // error on a still-connected session is ignored and retried next tick.
            // Guard the IsConnected read too: the monitor may be disposed concurrently
            // by Disconnect(), and this runs on a threadpool (timer) thread.
            try
            {
                if (!monitor.IsConnected)
                    SignalConnectionLost();
            }
            catch { /* monitor torn down — nothing to do */ }
        }
    }

    private void SignalConnectionLost()
    {
        if (Interlocked.Exchange(ref _connectionLostSignaled, 1) == 1) return;
        try { _healthTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        LoggingService.Warn("ConnectionLost",
            "The SSH session dropped while mounted; recovery is available via Reconnect.",
            _profile);
        ConnectionLost?.Invoke();
    }

    public void Disconnect()
    {
        try { _healthTimer?.Dispose(); } catch { }
        try { _monitor?.Disconnect(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
    }

    private AuthenticationMethod[] BuildAuthMethods()
    {
        if (!string.IsNullOrEmpty(_profile.PrivateKeyPath))
        {
            var passphrase = CredentialHelper.Decrypt(_profile.PrivateKeyPassphraseEncrypted);
            var keyFile    = PrivateKeyLoader.Load(_profile.PrivateKeyPath, passphrase);
            return [new PrivateKeyAuthenticationMethod(_profile.Username, keyFile)];
        }
        return [new PasswordAuthenticationMethod(_profile.Username,
                    CredentialHelper.Decrypt(_profile.PasswordEncrypted))];
    }

    private void VerifyHostKey(object? sender, HostKeyEventArgs e)
    {
        var presented = NormalizeHostKeyFingerprint(e.FingerPrintSHA256);
        var trusted = NormalizeHostKeyFingerprint(_profile.HostKeyFingerprint);

        if (string.IsNullOrWhiteSpace(trusted))
        {
            _profile.HostKeyFingerprint = presented;
            e.CanTrust = true;
            return;
        }

        e.CanTrust = string.Equals(trusted, presented, StringComparison.Ordinal);
        if (!e.CanTrust)
        {
            _hostKeyMismatchMessage =
                $"The SSH host key for '{_profile.Host}:{_profile.Port}' does not match " +
                "the trusted fingerprint saved for this connection.\n\n" +
                $"Trusted:  {trusted}\n" +
                $"Received: {presented}\n\n" +
                "This can happen after a legitimate server rebuild, but it can also indicate " +
                "a man-in-the-middle attack. Verify the server fingerprint before reconnecting.";
            LoggingService.Warn("HostKeyMismatch",
                $"Trusted fingerprint did not match received fingerprint. trusted={trusted}; received={presented}.",
                _profile);
        }
    }

    private static string? NormalizeHostKeyFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;

        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            value = value["SHA256:".Length..];

        return "SHA256:" + value.Trim().TrimEnd('=');
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string RemotePath(string fileName)
    {
        var base_    = _profile.RemotePath.TrimEnd('/');
        var relative = fileName.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(relative) ? base_ : $"{base_}/{relative}";
    }

    private static ulong ToFileTime(DateTime dt) => (ulong)dt.ToFileTimeUtc();

    // ── Attribute cache ───────────────────────────────────────────────────────

    private SftpFileAttributes GetAttributesCached(string remotePath)
    {
        if (_attrCache.TryGetValue(remotePath, out var entry) && DateTime.UtcNow < entry.Expires)
            return entry.Attrs;

        using var lease = _pool.Acquire();
        var attrs = lease.Client.GetAttributes(remotePath);
        _attrCache[remotePath] = (attrs, DateTime.UtcNow.Add(_attrCacheTtl));
        _negCache.TryRemove(remotePath, out _);
        return attrs;
    }

    /// <summary>
    /// Full invalidation — clears the file's own attr/neg cache AND the parent
    /// directory listing.  Use only for mutations (create, delete, rename, write,
    /// truncate, timestamp change) where the directory listing is stale.
    /// </summary>
    private void InvalidateCache(string remotePath)
    {
        _attrCache.TryRemove(remotePath, out _);
        _negCache.TryRemove(remotePath, out _);
        var trimmed = remotePath.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash > 0)
            _dirCache.TryRemove(trimmed[..lastSlash], out _);
    }

    /// <summary>
    /// Lightweight invalidation — clears only the file's own attr/neg cache.
    /// Does NOT touch the parent directory listing.  Use for Close and other
    /// non-mutating operations where the directory contents haven't changed.
    /// </summary>
    private void InvalidateFileOnly(string remotePath)
    {
        _attrCache.TryRemove(remotePath, out _);
        _negCache.TryRemove(remotePath, out _);
    }

    /// <summary>
    /// Returns true if the final path segment starts with '.' and dot-file hiding is active.
    /// Used to block access to Linux hidden files when the admin has enabled filtering.
    /// </summary>
    private bool IsDotFile(string fileName)
    {
        if (!_hideDotFiles) return false;
        var name = Path.GetFileName(fileName.TrimEnd('\\', '/'));
        return name.Length > 0 && name[0] == '.';
    }

    // ── FileInfo builders ─────────────────────────────────────────────────────

    private FileInfo BuildFileInfoFromAttrs(SftpFileAttributes attrs)
    {
        var info = new FileInfo
        {
            FileAttributes = attrs.IsDirectory
                ? (uint)System.IO.FileAttributes.Directory
                : (uint)System.IO.FileAttributes.Normal,
            CreationTime   = ToFileTime(attrs.LastWriteTime),
            LastWriteTime  = ToFileTime(attrs.LastWriteTime),
            LastAccessTime = ToFileTime(attrs.LastAccessTime),
            ChangeTime     = ToFileTime(attrs.LastWriteTime),
            FileSize       = (ulong)attrs.Size,
        };
        info.AllocationSize = (info.FileSize + 4095) & ~4095UL;
        return info;
    }

    private static FileInfo BuildFileInfo(ISftpFile f)
    {
        var info = new FileInfo
        {
            FileAttributes = f.IsDirectory
                ? (uint)System.IO.FileAttributes.Directory
                : (uint)System.IO.FileAttributes.Normal,
            CreationTime   = ToFileTime(f.LastWriteTime),
            LastWriteTime  = ToFileTime(f.LastWriteTime),
            LastAccessTime = ToFileTime(f.LastAccessTime),
            ChangeTime     = ToFileTime(f.LastWriteTime),
            FileSize       = (ulong)f.Attributes.Size,
        };
        info.AllocationSize = (info.FileSize + 4095) & ~4095UL;
        return info;
    }

    // ── Volume ────────────────────────────────────────────────────────────────

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = 1UL << 40;
        volumeInfo.FreeSize  = 1UL << 39;
        volumeInfo.SetVolumeLabel(_profile.VolumeLabel ?? _profile.Host);
        return STATUS_SUCCESS;
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    public override int GetSecurityByName(string fileName, out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        if (IsDotFile(fileName)) return STATUS_OBJECT_NAME_NOT_FOUND;

        var remotePath = RemotePath(fileName);

        // Negative cache: skip the network call if we recently confirmed this path
        // doesn't exist.  Explorer probes dozens of shell-metadata paths per folder
        // (desktop.ini, thumbs.db, *.lnk, Zone.Identifier, etc.) — this avoids
        // one SSH_FXP_STAT round-trip per probe.
        if (_negCache.TryGetValue(remotePath, out var negExpiry) && DateTime.UtcNow < negExpiry)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        try
        {
            var attrs = GetAttributesCached(remotePath);
            fileAttributes = attrs.IsDirectory
                ? (uint)System.IO.FileAttributes.Directory
                : (uint)System.IO.FileAttributes.Normal;
            return STATUS_SUCCESS;
        }
        catch
        {
            _negCache[remotePath] = DateTime.UtcNow.Add(_negCacheTtl);
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            fileInfo = BuildFileInfoFromAttrs(GetAttributesCached((string)fileNode));
            return STATUS_SUCCESS;
        }
        catch { return STATUS_OBJECT_NAME_NOT_FOUND; }
    }

    // ── Open / Create / Close ─────────────────────────────────────────────────

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        normalizedName = fileName;
        var path = RemotePath(fileName);
        fileNode = path;
        fileDesc = null!;
        fileInfo = default;
        if (IsDotFile(fileName)) return STATUS_OBJECT_NAME_NOT_FOUND;
        try
        {
            var attrs = GetAttributesCached(path);
            fileInfo  = BuildFileInfoFromAttrs(attrs);

            // For files: create a FileContext but do NOT open the SFTP handle yet.
            // The handle is opened lazily in Read/Write only if the caller actually
            // transfers data.  Explorer opens every file in a folder just for shell
            // metadata — without lazy open that would be an SSH_FXP_OPEN + SSH_FXP_CLOSE
            // round-trip per file, serialised over a single SSH channel = seconds of delay.
            if (!attrs.IsDirectory)
                fileDesc = new FileContext(_pool, path);

            return STATUS_SUCCESS;
        }
        catch (SshException ex) { return MapException(ex); }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[] securityDescriptor, ulong allocationSize,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        normalizedName = fileName;
        var path = RemotePath(fileName);
        fileNode = path; fileDesc = null!; fileInfo = default;
        if (_readOnly) return STATUS_ACCESS_DENIED;
        if (IsDotFile(fileName)) return STATUS_ACCESS_DENIED;
        bool isDir = (fileAttributes & (uint)System.IO.FileAttributes.Directory) != 0;
        try
        {
            if (isDir)
            {
                using var lease = _pool.Acquire();
                lease.Client.CreateDirectory(path);
            }
            else
            {
                // Open eagerly for Create — we know data will be written immediately.
                fileDesc = new FileContext(_pool, path, client =>
                {
                    try   { return client.Open(path, FileMode.Create, FileAccess.ReadWrite); }
                    catch { return client.Open(path, FileMode.Create, FileAccess.Write); }
                });
            }
            InvalidateCache(path);
            return GetFileInfo(path, fileDesc, out fileInfo);
        }
        catch (SshException ex) { return MapException(ex); }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        (fileDesc as FileContext)?.Dispose();
        // Do NOT invalidate any caches here.  Close is called for every file
        // Explorer opens for metadata — clearing even the attr cache would
        // force a fresh SFTP round-trip on the next GetSecurityByName/GetFileInfo.
        // The WinFsp kernel-level cache (FileInfoTimeout = 15s) plus our own
        // userspace _attrCache / _dirCache provide the necessary staleness window.
        // Actual mutations (Create, Delete, Rename, Write) invalidate caches
        // at their own call sites.
    }

    // ── Overwrite / Flush / SetBasicInfo ──────────────────────────────────────
    //
    // These callbacks are required for copy/paste to work.  Without them WinFsp
    // returns STATUS_INVALID_DEVICE_REQUEST ("Invalid MS-DOS function") when
    // Explorer copies files to the mounted drive.

    public override int Overwrite(object fileNode, object fileDesc,
        uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var path = (string)fileNode;
        if (_readOnly) return STATUS_ACCESS_DENIED;
        try
        {
            // Truncate the file to zero length — Explorer will Write the new content.
            if (fileDesc is FileContext ctx)
            {
                lock (ctx.Lock)
                {
                    var stream = ctx.GetOrOpenStreamUnsafe();
                    stream.SetLength(0);
                    ctx.ResetReadAhead();
                }
            }
            else
            {
                using var lease = _pool.Acquire();
                lease.Client.WriteAllBytes(path, []);
            }
            InvalidateCache(path);
            return GetFileInfo(path, fileDesc, out fileInfo);
        }
        catch (SshException ex) { return MapException(ex); }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        // SFTP has no explicit fsync — data is committed on write.
        // Just return cached FileInfo; don't force a round-trip.
        try
        {
            if (fileNode is string path)
                return GetFileInfo(path, fileDesc, out fileInfo);
            return STATUS_SUCCESS;
        }
        catch (SshException ex) { return MapException(ex); }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc,
        uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = default;
        var path = (string)fileNode;

        // During browsing, Explorer calls SetBasicInfo on every file just to
        // update the last-access time.  Pushing that to the SFTP server would
        // cost 2 round-trips (GetAttributes + SetAttributes) per file — the
        // #1 reason directory browsing was 10x slower than SSHFS-Win.
        //
        // Strategy: if only the access time is being set, accept silently
        // (SSHFS-Win does the same).  Only pay for a network call when the
        // caller is changing the *write* time (copy/paste, touch, etc.).

        bool hasWriteTime = lastWriteTime != 0 && lastWriteTime != ulong.MaxValue;
        if (_readOnly && hasWriteTime) return STATUS_ACCESS_DENIED;

        if (!hasWriteTime)
        {
            // Fast path — no network I/O, just return cached info.
            try { return GetFileInfo(path, fileDesc, out fileInfo); }
            catch { return STATUS_SUCCESS; }
        }

        // Slow path — actually push the timestamp to the server.
        try
        {
            using var lease = _pool.Acquire();
            var attrs = lease.Client.GetAttributes(path);

            attrs.LastWriteTime = DateTime.FromFileTimeUtc((long)lastWriteTime);

            if (lastAccessTime != 0 && lastAccessTime != ulong.MaxValue)
                attrs.LastAccessTime = DateTime.FromFileTimeUtc((long)lastAccessTime);

            lease.Client.SetAttributes(path, attrs);

            InvalidateFileOnly(path);
            return GetFileInfo(path, fileDesc, out fileInfo);
        }
        catch (SshException)
        {
            // Some SFTP servers reject timestamp changes — don't fail the copy.
            try { return GetFileInfo(path, fileDesc, out fileInfo); }
            catch { return STATUS_SUCCESS; }
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public override int Read(object fileNode, object fileDesc,
        IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var buf = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            if (fileDesc is FileContext ctx)
            {
                lock (ctx.Lock)
                {
                    // Try the prefetch buffer first — zero-cost if it matches.
                    int prefetched = ctx.TryReadFromPrefetch((long)offset, buf, 0, (int)length);
                    if (prefetched > 0)
                    {
                        Marshal.Copy(buf, 0, buffer, prefetched);
                        bytesTransferred = (uint)prefetched;
                        ctx.MaybeStartPrefetch((long)offset, prefetched);
                    }
                    else
                    {
                        var stream = ctx.GetOrOpenStreamUnsafe();
                        stream.Seek((long)offset, SeekOrigin.Begin);
                        int read = stream.Read(buf, 0, (int)length);
                        if (read > 0) Marshal.Copy(buf, 0, buffer, read);
                        bytesTransferred = (uint)read;
                        ctx.MaybeStartPrefetch((long)offset, read);
                    }
                }
            }
            else
            {
                using var lease = _pool.Acquire();
                using var s = lease.Client.OpenRead((string)fileNode);
                s.Seek((long)offset, SeekOrigin.Begin);
                int read = s.Read(buf, 0, (int)length);
                if (read > 0) Marshal.Copy(buf, 0, buffer, read);
                bytesTransferred = (uint)read;
            }
            return STATUS_SUCCESS;
        }
        catch (SshException ex) { return MapException(ex); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public override int Write(object fileNode, object fileDesc,
        IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0; fileInfo = default;
        if (_readOnly) return STATUS_ACCESS_DENIED;
        var path = (string)fileNode;
        var buf = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            if (fileDesc is FileContext ctx)
            {
                lock (ctx.Lock)
                {
                    var stream  = ctx.GetOrOpenStreamUnsafe();
                    long fileLen = stream.Length;

                    if (constrainedIo)
                    {
                        if ((long)offset >= fileLen)
                            return GetFileInfo(path, fileDesc, out fileInfo);
                        long available = fileLen - (long)offset;
                        if ((long)length > available) length = (uint)available;
                    }

                    Marshal.Copy(buffer, buf, 0, (int)length);

                    long writeAt = writeToEndOfFile ? fileLen : (long)offset;
                    stream.Seek(writeAt, SeekOrigin.Begin);
                    stream.Write(buf, 0, (int)length);
                    stream.Flush();
                    bytesTransferred = length;
                    ctx.ResetReadAhead();
                }
            }
            else
            {
                Marshal.Copy(buffer, buf, 0, (int)length);
                using var lease = _pool.Acquire();
                var existing = lease.Client.ReadAllBytes(path);
                long writeAt = writeToEndOfFile ? existing.Length : (long)offset;

                if (constrainedIo)
                {
                    if (writeAt >= existing.Length)
                        return GetFileInfo(path, fileDesc, out fileInfo);
                    long available = existing.Length - writeAt;
                    if ((long)length > available) length = (uint)available;
                }

                long newSize = Math.Max(existing.Length, writeAt + length);
                var  merged  = new byte[newSize];
                Buffer.BlockCopy(existing, 0, merged, 0, existing.Length);
                Buffer.BlockCopy(buf,      0, merged, (int)writeAt, (int)length);
                lease.Client.WriteAllBytes(path, merged);
                bytesTransferred = length;
            }

            InvalidateCache(path);
            return GetFileInfo(path, fileDesc, out fileInfo);
        }
        catch (SshException ex) { return MapException(ex); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // ── Directory listing ─────────────────────────────────────────────────────

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc,
        string pattern, string marker, ref object context,
        out string fileName, out FileInfo fileInfo)
    {
        fileName = default!;
        fileInfo = default;

        try
        {
            if (context is null)
            {
                var remoteDirPath = (string)fileNode;
                var entries = GetDirectoryEntriesCached(remoteDirPath);

                if (marker is not null)
                {
                    context = entries
                        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .SkipWhile(e => string.Compare(e.Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                        .GetEnumerator();
                }
                else
                {
                    context = ((IEnumerable<(string Name, FileInfo Info)>)entries).GetEnumerator();
                }
            }

            var enumerator = (IEnumerator<(string Name, FileInfo Info)>)context;
            if (enumerator.MoveNext())
            {
                fileName = enumerator.Current.Name;
                fileInfo = enumerator.Current.Info;
                return true;
            }

            return false;
        }
        catch (SftpPermissionDeniedException)
        {
            return false;
        }
        catch (SshException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a cached directory listing, fetching from the server only if the
    /// cache entry is missing or expired.  Also pre-warms the attribute cache
    /// from the SSH_FXP_READDIR response so subsequent GetSecurityByName /
    /// GetFileInfo calls for these entries are free.
    /// </summary>
    private List<(string Name, FileInfo Info)> GetDirectoryEntriesCached(string remoteDirPath)
    {
        if (_dirCache.TryGetValue(remoteDirPath, out var cached) && DateTime.UtcNow < cached.Expires)
            return cached.Entries;

        var attrExpiry = DateTime.UtcNow.Add(_attrCacheTtl);

        using var lease = _pool.Acquire();
        var entries = lease.Client
            .ListDirectory(remoteDirPath)
            .Where(e => e.Name is not "." and not "..")
            .Where(e => !_hideDotFiles || !e.Name.StartsWith('.'))
            .Select(e =>
            {
                var entryPath = $"{remoteDirPath.TrimEnd('/')}/{e.Name}";
                _attrCache[entryPath] = (e.Attributes, attrExpiry);
                _negCache.TryRemove(entryPath, out _);
                return (e.Name, BuildFileInfo(e));
            })
            .ToList();

        _dirCache[remoteDirPath] = (entries, DateTime.UtcNow.Add(_dirCacheTtl));
        return entries;
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
        => _readOnly ? STATUS_ACCESS_DENIED : STATUS_SUCCESS;

    // Cleanup is called by WinFsp when the last user-mode handle on a file/directory
    // is closed.  CleanupDelete is the flag that means "actually remove this object" —
    // CanDelete only checks permission; the real work belongs here.
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if ((flags & CleanupDelete) == 0) return;
        if (_readOnly) return;

        var path = (string)fileNode;  // fileNode is already the remote path (set in Open/Create)

        // Release the SFTP stream before issuing the delete so the server-side
        // file handle is closed first (some servers reject delete on open handles).
        (fileDesc as FileContext)?.Dispose();

        try
        {
            bool isDir = _attrCache.TryGetValue(path, out var cached) && cached.Attrs.IsDirectory;

            using var lease = _pool.Acquire();
            if (isDir)
                lease.Client.DeleteDirectory(path);
            else
                lease.Client.DeleteFile(path);

            InvalidateCache(path);
        }
        catch { /* Cleanup must never throw — WinFsp has no way to handle it */ }
    }

    public override int Rename(object fileNode, object fileDesc,
        string fileName, string newFileName, bool replaceIfExists)
    {
        if (_readOnly) return STATUS_ACCESS_DENIED;
        var oldPath = RemotePath(fileName);
        var newPath = RemotePath(newFileName);
        try
        {
            using var lease = _pool.Acquire();
            lease.Client.RenameFile(oldPath, newPath);
            InvalidateCache(oldPath);
            InvalidateCache(newPath);
            return STATUS_SUCCESS;
        }
        catch (SshException ex) { return MapException(ex); }
    }

    public override int SetFileSize(object fileNode, object fileDesc,
        ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        var path = (string)fileNode;

        if (_readOnly) return STATUS_ACCESS_DENIED;

        if (setAllocationSize)
            return GetFileInfo(path, fileDesc, out fileInfo);

        try
        {
            if (fileDesc is FileContext ctx)
            {
                lock (ctx.Lock)
                {
                    var stream = ctx.GetOrOpenStreamUnsafe();
                    stream.SetLength((long)newSize);
                }
            }
            else
            {
                using var lease = _pool.Acquire();
                var existing  = lease.Client.ReadAllBytes(path);
                var truncated = new byte[newSize];
                Buffer.BlockCopy(existing, 0, truncated, 0,
                    (int)Math.Min((ulong)existing.Length, newSize));
                lease.Client.WriteAllBytes(path, truncated);
            }
            InvalidateCache(path);
            return GetFileInfo(path, fileDesc, out fileInfo);
        }
        catch (SshException ex) { return MapException(ex); }
    }

    // ── Error mapping ─────────────────────────────────────────────────────────

    private static int MapException(SshException ex) => ex switch
    {
        SftpPermissionDeniedException => STATUS_ACCESS_DENIED,
        SftpPathNotFoundException     => STATUS_OBJECT_NAME_NOT_FOUND,
        _                             => STATUS_UNEXPECTED_IO_ERROR
    };
}

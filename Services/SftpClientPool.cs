using System.Collections.Concurrent;
using Renci.SshNet;

namespace DriveLink.Services;

/// <summary>
/// Maintains a pool of SFTP connections to the same host so that multiple
/// filesystem operations can proceed concurrently instead of serialising
/// through a single SSH channel.
///
/// The pool creates connections lazily up to <see cref="PoolSize"/> and
/// reuses idle ones via a LIFO stack (most-recently-used = warmest TCP).
///
/// When all pooled connections are busy, an overflow connection is created
/// on the spot and disposed when the lease ends (not returned to the pool).
/// This prevents deadlocks when many file handles are open simultaneously
/// during bulk copy operations.
/// </summary>
public sealed class SftpClientPool : IDisposable
{
    private readonly Func<SftpClient> _factory;
    private readonly ConcurrentStack<SftpClient> _idle = new();
    private int _poolCreated;
    private bool _disposed;

    /// <summary>Number of connections kept in the reusable pool.</summary>
    public int PoolSize { get; }

    public SftpClientPool(Func<SftpClient> factory, int poolSize = 4)
    {
        _factory  = factory;
        PoolSize  = poolSize;
    }

    /// <summary>
    /// Borrow an SFTP client.  Dispose the returned lease to return it.
    /// Never blocks — creates an overflow connection if the pool is exhausted.
    /// </summary>
    public Lease Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Try to grab an idle pooled connection (LIFO — warmest first).
        while (_idle.TryPop(out var client))
        {
            if (client.IsConnected)
                return new Lease(this, client, isOverflow: false);

            Interlocked.Decrement(ref _poolCreated);
            try { client.Dispose(); } catch { }
        }

        // 2. Pool has room — create a new pooled connection.
        if (Interlocked.Increment(ref _poolCreated) <= PoolSize)
        {
            var pooled = CreateClient();
            return new Lease(this, pooled, isOverflow: false);
        }

        // 3. Pool full — create an overflow connection that won't be pooled.
        //    This is the key difference: instead of spinning/blocking (which
        //    deadlocks when FileContexts hold leases and metadata ops need one),
        //    we just open a temporary extra connection.
        Interlocked.Decrement(ref _poolCreated); // didn't use a pool slot
        var overflow = CreateClient();
        return new Lease(this, overflow, isOverflow: true);
    }

    private SftpClient CreateClient()
    {
        var client = _factory();
        client.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // SSH.NET defaults to 32 KB SFTP buffers — each Read/Write is one
        // round-trip, so throughput ≈ bufferSize / latency.  On a 5 ms WiFi
        // link that's ~6 MB/s.  Bumping to 256 KB gets us into the 40-50 MB/s
        // range before encryption overhead becomes the limit.
        client.BufferSize = 256 * 1024;  // 256 KiB
        client.Connect();
        return client;
    }

    private void Return(SftpClient client, bool isOverflow)
    {
        if (isOverflow || _disposed || !client.IsConnected)
        {
            // Overflow connections are disposable — don't return to pool.
            if (!isOverflow)
                Interlocked.Decrement(ref _poolCreated);
            try { client.Disconnect(); } catch { }
            try { client.Dispose(); } catch { }
            return;
        }
        _idle.Push(client);
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryPop(out var client))
        {
            try { client.Disconnect(); } catch { }
            client.Dispose();
        }
    }

    /// <summary>
    /// RAII wrapper — dispose to return the client to the pool
    /// (or close it if it was an overflow connection).
    /// </summary>
    public readonly struct Lease : IDisposable
    {
        private readonly SftpClientPool _pool;
        private readonly bool _isOverflow;
        public SftpClient Client { get; }

        internal Lease(SftpClientPool pool, SftpClient client, bool isOverflow)
        {
            _pool       = pool;
            Client      = client;
            _isOverflow = isOverflow;
        }

        public void Dispose() => _pool.Return(Client, _isOverflow);
    }
}

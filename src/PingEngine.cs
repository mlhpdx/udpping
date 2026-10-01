using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UdpPingGraph;

/// <summary>
/// Sends UDP echo probes on a fixed cadence, matches replies to requests
/// by a 16-byte GUID payload, and buckets round-trip times (and drops)
/// into display ticks keyed by when each probe was SENT.
/// </summary>
internal sealed class PingEngine : IDisposable
{
    /// <summary>Width of one graph column, and the UI refresh cadence.</summary>
    public int TickMs => _options.DisplayIntervalMs;

    private readonly CliOptions _options;
    private readonly IPEndPoint _endpoint;
    private readonly Socket _socket;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _timeoutTicks;
    private readonly long _intervalTicks;

    private readonly ConcurrentDictionary<Guid, PendingProbe> _pending = new();
    private readonly ConcurrentDictionary<int, TickData> _ticks = new();

    private long _sentCount;
    private long _lostCount;
    private double _lastRttMs = double.NaN;

    public PingEngine(CliOptions options, IPEndPoint endpoint)
    {
        _options = options;
        _endpoint = endpoint;
        _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // A connected UDP socket lets us Send() without respecifying the
        // destination each call, and surfaces ICMP port-unreachable as a
        // socket error on the receive path.
        _socket.Connect(endpoint);
        _timeoutTicks = (long)(options.TimeoutMs / 1000.0 * Stopwatch.Frequency);
        _intervalTicks = (long)(options.Interval.TotalSeconds * Stopwatch.Frequency);
    }

    public long SentCount => Interlocked.Read(ref _sentCount);
    public long LostCount => Interlocked.Read(ref _lostCount);
    public double LastRttMs => Volatile.Read(ref _lastRttMs);

    /// <summary>The tick that "now" falls into, using the engine's own clock as the time base.</summary>
    public int CurrentTickIndex => (int)(_clock.ElapsedMilliseconds / TickMs);

    public async Task RunAsync(CancellationToken ct)
    {
        // The send cadence runs on its own dedicated thread using a
        // hybrid sleep/spin pacing loop (see SendLoop); it is not bound by
        // the timer-queue granularity that a PeriodicTimer would impose.
        var sendThread = new Thread(() => SendLoop(ct))
        {
            IsBackground = true,
            Name = "udpping-send",
            Priority = ThreadPriority.AboveNormal,
        };
        sendThread.Start();

        var receiveTask = ReceiveLoopAsync(ct);
        var timeoutTask = TimeoutLoopAsync(ct);

        try
        {
            await Task.WhenAll(receiveTask, timeoutTask).ConfigureAwait(false);
        }
        finally
        {
            sendThread.Join();
        }
    }

    /// <summary>
    /// Paces sends against <see cref="_clock"/> deadlines. For each probe
    /// it sleeps while comfortably ahead of the next deadline, then busy-
    /// spins for the final sub-millisecond so the actual send lands close
    /// to the target time without burning a core continuously. Deadlines
    /// advance by a fixed interval, so timing does not drift over a run.
    /// </summary>
    private void SendLoop(CancellationToken ct)
    {
        // One spin budget: how close to the deadline we switch from
        // sleeping to spinning. 1ms of slack comfortably covers the OS
        // scheduler's wakeup jitter on Linux.
        var spinThresholdTicks = Stopwatch.Frequency / 1000; // ~1ms

        var nextDeadline = _clock.ElapsedTicks;
        var spinner = new SpinWait();

        while (!ct.IsCancellationRequested)
        {
            var now = _clock.ElapsedTicks;
            var remaining = nextDeadline - now;

            if (remaining > spinThresholdTicks)
            {
                // Far from the deadline: give the CPU back. Convert the
                // lead time (minus the spin budget) to whole milliseconds.
                var sleepMs = (int)((remaining - spinThresholdTicks) * 1000 / Stopwatch.Frequency);
                if (sleepMs > 0)
                {
                    if (ct.WaitHandle.WaitOne(sleepMs))
                    {
                        break; // cancellation signalled
                    }
                }
                continue;
            }

            if (remaining > 0)
            {
                // Final approach: spin until the deadline arrives.
                spinner.SpinOnce();
                continue;
            }

            spinner.Reset();
            SendOne();

            // Advance to the next deadline. If we have fallen far behind
            // (e.g. the process was descheduled), resync to now rather
            // than firing a burst to "catch up".
            nextDeadline += _intervalTicks;
            var drift = _clock.ElapsedTicks - nextDeadline;
            if (drift > _intervalTicks)
            {
                nextDeadline = _clock.ElapsedTicks + _intervalTicks;
            }
        }
    }

    private void SendOne()
    {
        var id = Guid.CreateVersion7();
        var tickIndex = CurrentTickIndex;
        var sentAt = _clock.ElapsedTicks;

        _pending[id] = new PendingProbe(tickIndex, sentAt);
        GetOrAddTick(tickIndex);
        Interlocked.Increment(ref _sentCount);

        Span<byte> payload = stackalloc byte[16];
        id.TryWriteBytes(payload);

        try
        {
            _socket.Send(payload, SocketFlags.None);
        }
        catch (SocketException)
        {
            // A synchronous send failure (e.g. immediate ICMP unreachable)
            // is treated as an instant drop rather than waiting for it to
            // age out of the pending table.
            if (_pending.TryRemove(id, out var probe))
            {
                GetOrAddTick(probe.TickIndex).AddTimedOut();
                Interlocked.Increment(ref _lostCount);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Reused receive buffer; a UDP echo reply is exactly our 16-byte
        // GUID, and anything of a different length is ignored.
        var buffer = new byte[64];

        while (!ct.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await _socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // Typically a port-unreachable ICMP surfaced on the socket.
                // Not fatal - just keep listening for the next reply.
                continue;
            }

            if (received != 16)
            {
                continue; // not one of ours
            }

            var id = new Guid(buffer.AsSpan(0, 16));
            if (!_pending.TryRemove(id, out var probe))
            {
                continue; // unknown, duplicate, or already timed out
            }

            var elapsedTicks = _clock.ElapsedTicks - probe.SentTimestamp;
            var rttMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

            Volatile.Write(ref _lastRttMs, rttMs);
            GetOrAddTick(probe.TickIndex).AddCompleted(rttMs);
        }
    }

    private async Task TimeoutLoopAsync(CancellationToken ct)
    {
        // Polls faster than the tick width so drops appear promptly
        // without needing a dedicated timer per packet.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = _clock.ElapsedTicks;

            foreach (var (id, probe) in _pending)
            {
                if (now - probe.SentTimestamp < _timeoutTicks)
                {
                    continue;
                }

                if (!_pending.TryRemove(id, out _))
                {
                    continue; // a reply resolved it concurrently
                }

                GetOrAddTick(probe.TickIndex).AddTimedOut();
                Interlocked.Increment(ref _lostCount);
            }
        }
    }

    private TickData GetOrAddTick(int index) => _ticks.GetOrAdd(index, static _ => new TickData());

    /// <summary>
    /// Builds render-time snapshots for a contiguous range of ticks in a
    /// single pass, including each tick's in-flight pending count. Doing
    /// the whole range at once means the pending table is scanned exactly
    /// once per render rather than once per column.
    /// </summary>
    public void FillTickSnapshots(int startTick, Span<TickSnapshot> snapshots)
    {
        var count = snapshots.Length;

        for (var i = 0; i < count; i++)
        {
            var index = startTick + i;
            var (completed, timedOut) = _ticks.TryGetValue(index, out var data)
                ? data.Snapshot()
                : ([], 0);
            snapshots[i] = new TickSnapshot(index, completed, timedOut, 0);
        }

        // Single pass over the pending table, tallying each probe into its
        // column if it falls within the visible range.
        foreach (var probe in _pending.Values)
        {
            var col = probe.TickIndex - startTick;
            if ((uint)col < (uint)count)
            {
                var s = snapshots[col];
                snapshots[col] = s with { Pending = s.Pending + 1 };
            }
        }
    }

    public void Dispose() => _socket.Dispose();
}

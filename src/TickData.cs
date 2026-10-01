namespace UdpPingGraph;

/// <summary>
/// Mutable, thread-safe accumulator for the packets whose SEND time
/// falls into a single 500ms tick. Completed round-trip times, and a
/// count of packets that timed out, are recorded here as they resolve -
/// which can happen well after the tick was first drawn.
/// </summary>
internal sealed class TickData
{
    private readonly object _gate = new();
    private readonly List<double> _completedMs = [];
    private int _timedOut;

    public void AddCompleted(double rttMs)
    {
        lock (_gate)
        {
            _completedMs.Add(rttMs);
        }
    }

    public void AddTimedOut() => Interlocked.Increment(ref _timedOut);

    public (double[] Completed, int TimedOut) Snapshot()
    {
        lock (_gate)
        {
            return (_completedMs.ToArray(), Volatile.Read(ref _timedOut));
        }
    }
}

/// <summary>A read-only view of one tick's state at render time.</summary>
internal readonly record struct TickSnapshot(int Index, double[] CompletedMs, int TimedOut, int Pending);

/// <summary>Bookkeeping for a packet that has been sent but not yet resolved.</summary>
internal readonly record struct PendingProbe(int TickIndex, long SentTimestamp);

/// <summary>
/// Min/max/median across every completed RTT currently visible, computed
/// in one gather-and-sort pass over the render snapshots (no LINQ, no
/// per-column flattening). <see cref="Min"/>/<see cref="Max"/>/
/// <see cref="Median"/> are <see cref="double.NaN"/> when nothing has
/// completed yet.
/// </summary>
internal readonly struct AggregateStats
{
    public int Count { get; private init; }
    public double Min { get; private init; }
    public double Max { get; private init; }
    public double Median { get; private init; }

    public static AggregateStats FromSnapshots(ReadOnlySpan<TickSnapshot> snapshots)
    {
        var total = 0;
        foreach (var snap in snapshots)
        {
            total += snap.CompletedMs.Length;
        }

        if (total == 0)
        {
            return new AggregateStats { Count = 0, Min = double.NaN, Max = double.NaN, Median = double.NaN };
        }

        var all = new double[total];
        var offset = 0;
        foreach (var snap in snapshots)
        {
            var src = snap.CompletedMs;
            Array.Copy(src, 0, all, offset, src.Length);
            offset += src.Length;
        }

        Array.Sort(all);

        var median = total % 2 == 1
            ? all[total / 2]
            : (all[total / 2 - 1] + all[total / 2]) / 2.0;

        return new AggregateStats
        {
            Count = total,
            Min = all[0],
            Max = all[^1],
            Median = median,
        };
    }
}

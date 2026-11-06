using System.Diagnostics;

namespace SerialTool.Core;

/// <summary>
/// Maps monotonic <see cref="Stopwatch"/> timestamps to wall-clock time.
/// The wall clock is sampled once at session start; DST changes and drift are ignored by design.
/// </summary>
public sealed class SessionClock
{
    public SessionClock() : this(DateTime.Now, Stopwatch.GetTimestamp(), Stopwatch.Frequency) { }

    public SessionClock(DateTime startLocal, long startTicks, long frequency)
    {
        StartLocal = startLocal;
        StartTicks = startTicks;
        Frequency = frequency;
    }

    public DateTime StartLocal { get; }
    public long StartTicks { get; }
    public long Frequency { get; }

    public TimeSpan Elapsed(long timestamp) => TicksToSpan(timestamp - StartTicks);

    public DateTime ToLocal(long timestamp) => StartLocal + Elapsed(timestamp);

    public TimeSpan TicksToSpan(long ticks) =>
        TimeSpan.FromTicks((long)(ticks * ((double)TimeSpan.TicksPerSecond / Frequency)));
}

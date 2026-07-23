using System.Collections.Concurrent;

namespace Andy.Containers.Messaging.Events;

/// <summary>
/// Allocates a monotonic sequence shared by lifecycle and output events for
/// one run. The wall-clock floor keeps a restarted process ahead of sequences
/// emitted before restart; the atomic update orders concurrent publishers.
/// </summary>
public static class RunEventSequence
{
    private static readonly ConcurrentDictionary<Guid, long> LastByRun = new();

    public static long Next(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Run id must be non-empty.", nameof(runId));
        }

        var clockFloor = DateTimeOffset.UtcNow.UtcTicks;
        return LastByRun.AddOrUpdate(
            runId,
            clockFloor,
            (_, current) => Math.Max(clockFloor, checked(current + 1)));
    }
}

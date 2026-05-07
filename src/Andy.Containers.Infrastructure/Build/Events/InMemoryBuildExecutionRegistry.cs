using System.Collections.Concurrent;
using Andy.Containers.Storage;

namespace Andy.Containers.Infrastructure.Build.Events;

/// <summary>
/// Process-local <see cref="IBuildExecutionRegistry"/> backed by a
/// concurrent dictionary. Singleton; no persistence, no eviction
/// policy yet (build records linger until the process restarts).
/// </summary>
/// <remarks>
/// IM9 (rivoli-ai/andy-containers#263). The registry intentionally
/// keeps everything; a real production deployment will need an
/// eviction strategy (TTL, max-entries, LRU) once the volume of
/// completed builds is non-trivial. Tracked as a follow-up; not
/// blocking M1.9.
/// </remarks>
public sealed class InMemoryBuildExecutionRegistry : IBuildExecutionRegistry
{
    private readonly ConcurrentDictionary<Guid, BuildExecutionState> _states = new();

    public BuildExecutionState Register(Guid buildId, ImageBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = new BuildExecutionState
        {
            BuildId = buildId,
            TemplateId = request.TemplateId,
            Status = BuildExecutionStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow,
        };
        if (!_states.TryAdd(buildId, state))
        {
            throw new InvalidOperationException(
                $"build id {buildId} is already registered — caller must generate a fresh id per build.");
        }
        return state;
    }

    public void Update(Guid buildId, BuildExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _states[buildId] = state;
    }

    public BuildExecutionState? TryGet(Guid buildId)
        => _states.TryGetValue(buildId, out var state) ? state : null;
}

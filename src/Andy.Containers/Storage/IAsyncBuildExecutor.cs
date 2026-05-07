namespace Andy.Containers.Storage;

/// <summary>
/// Wraps <see cref="IImageBuildOrchestrator"/> in an async-by-design
/// envelope: cache hits short-circuit synchronously, cache misses
/// queue a background task and return immediately with a build id
/// the caller can poll or attach to via SSE.
/// </summary>
/// <remarks>
/// IM9 (rivoli-ai/andy-containers#263). The split lets the API
/// controller hand off to one type instead of branching internally —
/// it always calls <see cref="StartAsync"/> and surfaces the
/// returned <see cref="AsyncBuildHandle"/> as a <c>BuildHandle</c>
/// to clients.
/// </remarks>
public interface IAsyncBuildExecutor
{
    /// <summary>
    /// Begin a build. Returns a handle whose <see cref="AsyncBuildHandle.Status"/>
    /// is one of:
    /// <list type="bullet">
    ///   <item><description><c>cached</c> — synchronous cache hit; <see cref="AsyncBuildHandle.Result"/> is populated.</description></item>
    ///   <item><description><c>queued</c> — background task launched; subscribe via the event bus or poll the registry.</description></item>
    ///   <item><description><c>failed</c> — synchronous setup failure (e.g. unknown template); <see cref="AsyncBuildHandle.Result"/> carries the error.</description></item>
    /// </list>
    /// </summary>
    Task<AsyncBuildHandle> StartAsync(ImageBuildRequest request, CancellationToken ct);
}

/// <summary>
/// Result of <see cref="IAsyncBuildExecutor.StartAsync"/>.
/// </summary>
public sealed record AsyncBuildHandle(
    Guid BuildId,
    AsyncBuildHandleStatus Status,
    BuildResult? Result);

public enum AsyncBuildHandleStatus
{
    /// <summary>Cache hit — <see cref="AsyncBuildHandle.Result"/> populated, no background work.</summary>
    Cached,
    /// <summary>Background task started — caller should subscribe / poll for completion.</summary>
    Queued,
    /// <summary>Synchronous setup failure (template missing, registry not configured).</summary>
    Failed,
}

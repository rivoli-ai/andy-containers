using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// High-level container orchestration service.
/// Handles the full lifecycle from template selection through provisioning to cleanup.
/// </summary>
public interface IContainerService
{
    Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default);
    Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct = default);
    Task StartContainerAsync(Guid containerId, CancellationToken ct = default);
    Task StopContainerAsync(Guid containerId, CancellationToken ct = default);
    Task DestroyContainerAsync(Guid containerId, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// F4.1 (rivoli-ai/conductor#1934). Streaming variant of
    /// <see cref="ExecAsync(Guid, string, TimeSpan, CancellationToken)"/>:
    /// the same exec, but each stdout/stderr line is surfaced to
    /// <paramref name="onLine"/> as it is produced rather than buffered
    /// until the process exits. Returns the same terminal
    /// <see cref="ExecResult"/> (with the full buffered stdout/stderr)
    /// so existing callers keep their exit-code + final-output contract.
    /// </summary>
    /// <remarks>
    /// Honours decision #17 (no new Docker-Engine verb beyond the
    /// existing exec/attach surface) — this is the same
    /// <c>ExecCreate</c> + <c>StartAndAttach</c> path, read incrementally.
    /// The default implementation delegates to the buffered overload and
    /// then replays the final stdout/stderr as line callbacks, so
    /// providers that can't stream (Apple, cloud) still deliver every
    /// line — just all at once at the end rather than mid-run. Docker
    /// overrides this to deliver lines live.
    /// </remarks>
    async Task<ExecResult> ExecStreamingAsync(
        Guid containerId,
        string command,
        TimeSpan timeout,
        Action<ExecOutputChunk> onLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        var result = await ExecAsync(containerId, command, timeout, ct);
        foreach (var line in SplitLines(result.StdOut))
        {
            onLine(new ExecOutputChunk(ExecStreamKind.Stdout, line));
        }
        foreach (var line in SplitLines(result.StdErr))
        {
            onLine(new ExecOutputChunk(ExecStreamKind.Stderr, line));
        }
        return result;
    }

    private static IEnumerable<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            // Skip a trailing empty fragment from a final newline so we
            // don't emit a spurious blank line at end-of-output.
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// Asynchronous-callback variant used by streaming transports that need
    /// to await each output write and apply backpressure.
    /// </summary>
    Task<ExecResult> ExecStreamingAsync(
        Guid containerId,
        string command,
        TimeSpan timeout,
        Func<ExecOutputChunk, CancellationToken, ValueTask> onLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return ExecStreamingAsync(
            containerId,
            command,
            timeout,
            chunk => onLine(chunk, ct).AsTask().GetAwaiter().GetResult(),
            ct);
    }

    Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct = default);
    Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default);
    Task ResizeContainerAsync(Guid containerId, ResourceSpec resources, CancellationToken ct = default);

    /// <summary>
    /// F6.4 (rivoli-ai/conductor#1943). Publishes a container TCP port to a
    /// host (loopback) port for the run's web preview, returning the mapping.
    /// Throws <see cref="NotSupportedException"/> for providers that cannot
    /// add a live mapping (Docker on a running container, Apple, cloud) — the
    /// API surfaces that as a 400. Honours decision #17 (no new
    /// Docker-Engine verb).
    /// </summary>
    Task<MappedPort> ExposePortAsync(Guid containerId, int containerPort, CancellationToken ct = default);
}

public class CreateContainerRequest
{
    public required string Name { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateCode { get; set; }
    public Guid? ProviderId { get; set; }
    public string? ProviderCode { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? OwnerId { get; set; }
    public ResourceSpec? Resources { get; set; }
    public GpuSpec? Gpu { get; set; }
    public GitRepositoryConfig? GitRepository { get; set; }
    public List<GitRepositoryConfig>? GitRepositories { get; set; }
    public bool ExcludeTemplateRepos { get; set; }
    public bool SkipUrlValidation { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public Models.CodeAssistantConfig? CodeAssistant { get; set; }
    public bool ExcludeTemplateCodeAssistant { get; set; }
    public TimeSpan? ExpiresAfter { get; set; }
    public CreationSource Source { get; set; } = CreationSource.Unknown;
    public string? ClientInfo { get; set; }
    public string? OwnerEmail { get; set; }
    public string? OwnerPreferredUsername { get; set; }

    // Optional correlation to a backlog story. When set, the container's
    // run.* lifecycle events (finished/failed/cancelled) carry this id so
    // the caller (e.g. andy-issues) can tie the run back to a UserStory.
    public Guid? StoryId { get; set; }

    // rivoli-ai/conductor#1947. Per-run token attribution for headless
    // agent runs. When set, these are injected into the container env as
    // ANDY_RUN_ID / ANDY_TASK_ID / ANDY_AGENT_ID. The in-container agent
    // forwards them as X-Andy-Run-Id / X-Andy-Task-Id / X-Andy-Agent-Id
    // on every andy-models proxy call, so the resulting UsageEvent ledger
    // rows and gen_ai.client.* metric records are attributable to this
    // run/task/agent. All nullable — non-headless / non-run containers
    // leave them unset and the env vars are simply omitted.
    public string? RunId { get; set; }
    public string? TaskId { get; set; }
    public string? AttributionAgentId { get; set; }

    /// <summary>
    /// X4 (rivoli-ai/andy-containers#93). When set, the bound
    /// <c>EnvironmentProfile</c> overrides the template's base image
    /// (with <c>profile.BaseImageRef</c>) and the GUI sidecar
    /// behaviour (Headless/Terminal → no VNC; Desktop → VNC). The
    /// template still supplies resources, scripts, and dependencies.
    /// X5 wires this from the workspace-create surface; here the
    /// pipeline just propagates whatever the caller sets.
    /// </summary>
    public Guid? EnvironmentProfileId { get; set; }
}

public class GitRepositoryConfig
{
    public required string Url { get; set; }
    public string? Branch { get; set; }
    public string? CredentialRef { get; set; }
    public string? TargetPath { get; set; }
    public int? CloneDepth { get; set; }
    public bool Submodules { get; set; }
}

public class ContainerFilter
{
    public string? OwnerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public ContainerStatus? Status { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? ProviderId { get; set; }
    public CreationSource? Source { get; set; }
    public int? Skip { get; set; }
    public int? Take { get; set; }
}

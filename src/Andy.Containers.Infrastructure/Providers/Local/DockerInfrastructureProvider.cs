using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;

namespace Andy.Containers.Infrastructure.Providers.Local;

public class DockerInfrastructureProvider : IInfrastructureProvider
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerInfrastructureProvider> _logger;
    private readonly string _endpoint;

    public ProviderType Type => ProviderType.Docker;

    public DockerInfrastructureProvider(string? connectionConfig, ILogger<DockerInfrastructureProvider> logger)
    {
        _logger = logger;
        _endpoint = ResolveDockerEndpoint(connectionConfig);
        _logger.LogDebug("Using Docker endpoint: {Endpoint}", _endpoint);
        _client = new DockerClientConfiguration(new Uri(_endpoint)).CreateClient();
    }

    private static string ResolveDockerEndpoint(string? connectionConfig)
    {
        // Try explicit configuration first, but only if the socket actually exists
        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("endpoint", out var ep))
                {
                    var configured = ep.GetString();
                    if (!string.IsNullOrEmpty(configured))
                    {
                        // For unix sockets, verify the file exists before committing to it
                        if (configured.StartsWith("unix://"))
                        {
                            var socketPath = configured["unix://".Length..];
                            if (File.Exists(socketPath))
                                return configured;
                            // Socket from config not found — fall through to auto-discovery
                        }
                        else
                        {
                            // TCP or other endpoints — trust the configuration
                            return configured;
                        }
                    }
                }
            }
            catch { }
        }

        // Auto-discover: default socket path
        const string defaultSocket = "/var/run/docker.sock";
        if (File.Exists(defaultSocket))
            return $"unix://{defaultSocket}";

        // macOS Docker Desktop places the socket under ~/.docker/run/
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(homeDir))
        {
            var dockerDesktopSocket = Path.Combine(homeDir, ".docker/run/docker.sock");
            if (File.Exists(dockerDesktopSocket))
                return $"unix://{dockerDesktopSocket}";
        }

        // Fallback to default even if not found — let HealthCheck report Unreachable
        return $"unix://{defaultSocket}";
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.Docker,
            SupportedArchitectures = ["arm64", "amd64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 8,
            MaxMemoryMb = 16384,
            MaxDiskGb = 100,
            SupportsGpu = false,
            SupportsVolumeMount = true,
            SupportsPortForwarding = true,
            SupportsExec = true,
            SupportsStreaming = true,
            SupportsOfflineBuild = true
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            await _client.System.PingAsync(ct);
            return ProviderHealth.Healthy;
        }
        catch
        {
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        // Existing templates may still persist the old mutable `:latest`
        // spelling. Canonicalise again at the provider boundary so direct
        // provider callers cannot accidentally reuse stale legacy content.
        var imageReference = Andy.Containers.Validation.LocalImages.IsAgentCli(spec.ImageReference)
            ? Andy.Containers.Validation.LocalImages.AgentCli
            : spec.ImageReference;
        _logger.LogInformation("Creating Docker container {Name} from {Image}", spec.Name, imageReference);

        var containerName = spec.Name.ToLowerInvariant().Replace(' ', '-');

        // Remove any existing container with the same name (stopped or running)
        try
        {
            var existing = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [$"^/{containerName}$"] = true }
                }
            }, ct);

            foreach (var old in existing)
            {
                _logger.LogInformation("Removing existing container {Id} with name {Name}", old.ID[..12], containerName);
                await _client.Containers.RemoveContainerAsync(old.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for existing container {Name}", containerName);
        }

        // Locally-built images have a stronger contract than registry images:
        // they must be present in THIS daemon and must never silently fall
        // through to a mutable registry pull when a local build fails.
        bool imageExists = false;
        if (Andy.Containers.Validation.LocalImages.IsLocallyBuilt(imageReference))
        {
            try
            {
                await EnsureLocalImageAsync(imageReference, ct);
                imageExists = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception buildEx)
            {
                _logger.LogError(buildEx, "Failed to ensure local image {Image}", imageReference);
                throw new InvalidOperationException(
                    $"Locally-built image '{imageReference}' could not be built and verified.",
                    buildEx);
            }
        }
        else
        {
            try
            {
                await _client.Images.InspectImageAsync(imageReference, ct);
                imageExists = true;
            }
            catch (DockerImageNotFoundException)
            {
                // fall through to registry pull
            }
        }

        if (!imageExists)
        {
            // Registry-backed image: pull only after a confirmed local miss.
            try
            {
                await _client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = imageReference },
                    null,
                    new Progress<JSONMessage>(m => _logger.LogDebug("Pull: {Status}", m.Status)),
                    ct);

                // rivoli-ai/andy-containers#125. Audit-log the resolved
                // RepoDigests after a successful pull so operators see
                // exactly which content-addressed blob the daemon accepted.
                try
                {
                    var inspect = await _client.Images.InspectImageAsync(imageReference, ct);
                    var resolvedDigest = inspect.RepoDigests is { Count: > 0 }
                        ? inspect.RepoDigests[0]
                        : "(no repo digest reported)";
                    _logger.LogInformation(
                        "Pulled image {Image}; resolved digest: {Digest}",
                        imageReference, resolvedDigest);
                }
                catch (Exception inspectEx)
                {
                    // Best-effort; never fail provisioning because the audit
                    // lookup did not pan out.
                    _logger.LogDebug(inspectEx,
                        "Could not inspect resolved digest for {Image}", imageReference);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pull image {Image}", imageReference);
            }
        }

        var portBindings = new Dictionary<string, IList<PortBinding>>();
        var exposedPorts = new Dictionary<string, EmptyStruct>();
        if (spec.PortMappings is not null)
        {
            foreach (var (container, host) in spec.PortMappings)
            {
                var key = $"{container}/tcp";
                exposedPorts[key] = default;
                portBindings[key] = new List<PortBinding> { new() { HostPort = host.ToString() } };
            }
        }

        // Build the command: use spec.Command if provided, otherwise default to
        // "sleep infinity" to keep the container alive as a dev environment.
        var cmd = new List<string>();
        if (!string.IsNullOrEmpty(spec.Command))
        {
            cmd.Add(spec.Command);
            if (spec.Arguments is not null)
                cmd.AddRange(spec.Arguments);
        }
        else
        {
            cmd.AddRange(["sleep", "infinity"]);
        }

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = imageReference,
            Name = containerName,
            Cmd = cmd,
            Env = spec.EnvironmentVariables?.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            ExposedPorts = exposedPorts,
            Labels = spec.Labels,
            HostConfig = new HostConfig
            {
                PortBindings = portBindings,
                Memory = (long)(spec.Resources?.MemoryMb ?? 4096) * 1024 * 1024,
                NanoCPUs = (long)((spec.Resources?.CpuCores ?? 2) * 1e9),
                // Cap PID count so a fork bomb inside the container cannot DoS
                // the host. 4096 leaves plenty of headroom for parallel builds
                // and language servers.
                PidsLimit = 4096,
                // Block setuid-driven privilege escalation; closes the easiest
                // post-exploitation path for an attacker who lands in the
                // container.
                SecurityOpt = new List<string> { "no-new-privileges:true" },
                // Drop capabilities that are not needed by typical dev workloads
                // and have known abuse paths: NET_RAW (ARP spoofing / raw
                // packets), MKNOD (creating device nodes).
                CapDrop = new List<string> { "NET_RAW", "MKNOD" }
            }
        }, ct);

        await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters(), ct);

        _logger.LogInformation("Docker container {Id} created and started", response.ID);

        return new ContainerProvisionResult
        {
            ExternalId = response.ID,
            Status = ContainerStatus.Running,
            ConnectionInfo = await GetConnectionInfoAsync(response.ID, ct)
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        await _client.Containers.StartContainerAsync(externalId, new ContainerStartParameters(), ct);
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        try
        {
            await _client.Containers.StopContainerAsync(externalId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Phantom container — already gone from the daemon's
            // perspective is the same as "stopped" for our purposes.
            // Mirrors the symmetric handling in DestroyContainerAsync.
            // Conductor #826 item 3.
            _logger.LogWarning(
                "[CONTAINERS-STOP] phantom container {ExternalId} — daemon reports not-found, treating as already stopped",
                externalId);
        }
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        try
        {
            await _client.Containers.RemoveContainerAsync(externalId, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Phantom container: the andy-containers DB row still
            // references this externalId, but the docker daemon has
            // already removed it (out-of-band `docker rm`, host
            // reboot, daemon restart with prune, …). The goal of
            // DestroyContainer is "make this container be gone" —
            // it already is. Treat as success so the orchestration
            // layer can flip the DB row to Destroyed and the user
            // sees their phantom cleared. Conductor #826 item 3.
            _logger.LogWarning(
                "[CONTAINERS-DESTROY] phantom container {ExternalId} — daemon reports not-found, marking destroyed",
                externalId);
        }
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        ContainerInspectResponse inspect;
        try
        {
            inspect = await _client.Containers.InspectContainerAsync(externalId, ct);
        }
        catch (DockerContainerNotFoundException ex)
        {
            // The docker daemon has no such container — it was removed
            // out-of-band (e.g. a later create reused the container name and
            // removed this one, host reboot, prune). Surface a PROVIDER-NEUTRAL
            // "not found" so reconcilers (ContainerStatusSyncWorker) flip the
            // stale DB row to Destroyed instead of busy-looping on the phantom
            // forever. Docker.DotNet throws DockerContainerNotFoundException
            // (a DockerApiException, NOT an InvalidOperationException), which
            // the sync worker's message-based catch never matched — this
            // translation is what makes that catch fire. rivoli-ai/conductor
            // dead-container poll loop fix.
            throw new InvalidOperationException(
                $"Container {externalId} not found on the docker daemon", ex);
        }
        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = inspect.State.Running ? ContainerStatus.Running : ContainerStatus.Stopped,
            StartedAt = inspect.State.StartedAt != default ? DateTime.Parse(inspect.State.StartedAt) : null,
            IpAddress = inspect.NetworkSettings?.IPAddress
        };
    }

    public async Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        await _client.Containers.UpdateContainerAsync(externalId, new ContainerUpdateParameters
        {
            Memory = (long)resources.MemoryMb * 1024 * 1024,
            NanoCPUs = (long)(resources.CpuCores * 1e9)
        }, ct);
        return new ContainerProvisionResult { ExternalId = externalId, Status = ContainerStatus.Running };
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var inspect = await _client.Containers.InspectContainerAsync(externalId, ct);
        var ports = new Dictionary<int, int>();
        if (inspect.NetworkSettings?.Ports is not null)
        {
            foreach (var (key, bindings) in inspect.NetworkSettings.Ports)
            {
                if (bindings is not null && bindings.Count > 0 && int.TryParse(key.Split('/')[0], out var containerPort))
                {
                    if (int.TryParse(bindings[0].HostPort, out var hostPort))
                        ports[containerPort] = hostPort;
                }
            }
        }

        // F6.4 (#1943): every mapped port that isn't a reserved
        // IDE/VNC/SSH endpoint is a candidate web app the run published —
        // surface it as a loopback URL Conductor can preview via the
        // UnifiedProxy.
        var webApps = ports
            .Where(kv => !ReservedContainerPorts.Contains(kv.Key))
            .OrderBy(kv => kv.Key)
            .Select(kv => $"http://localhost:{kv.Value}")
            .ToList();

        return new ConnectionInfo
        {
            IpAddress = inspect.NetworkSettings?.IPAddress,
            PortMappings = ports,
            IdeEndpoint = ports.TryGetValue(8080, out var idePort) ? $"https://localhost:{idePort}" : null,
            VncEndpoint = ports.TryGetValue(6080, out var vncPort) ? $"https://localhost:{vncPort}" : null,
            SshEndpoint = ports.TryGetValue(22, out var sshPort) ? $"ssh root@localhost -p {sshPort}" : null,
            WebAppEndpoints = webApps,
        };
    }

    /// <summary>
    /// Container ports reserved for the IDE (8080), noVNC (6080) and SSH
    /// (22) endpoints — excluded from the generic web-app preview list.
    /// F6.4 (#1943).
    /// </summary>
    internal static readonly HashSet<int> ReservedContainerPorts = new() { 22, 6080, 8080 };

    /// <summary>
    /// F6.4 (#1943). Docker cannot publish a new port on an already-running
    /// container — it would require recreating it. We surface this as the
    /// same <see cref="NotSupportedException"/>→400 the API uses for live
    /// resource resize, telling the caller to publish the port at
    /// create-time. Ports published at create-time are already returned by
    /// <see cref="GetConnectionInfoAsync"/>. No new Docker-Engine verb
    /// (decision #17).
    /// </summary>
    public Task<MappedPort> ExposePortAsync(string externalId, int containerPort, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"Docker cannot publish container port {containerPort} on a running container; " +
            "the container must be recreated with the port published. " +
            "Ports published when the container was created are returned by GET /ports.");

    public async Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct)
    {
        var inspect = await _client.Containers.InspectContainerAsync(externalId, ct);

        // Get a single stats snapshot (stream: false)
        var statsResponse = new ContainerStatsResponse();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await _client.Containers.GetContainerStatsAsync(externalId,
            new ContainerStatsParameters { Stream = false },
            new Progress<ContainerStatsResponse>(s => statsResponse = s),
            cts.Token);

        // CPU %
        double cpuPercent = 0;
        if (statsResponse.CPUStats?.CPUUsage != null && statsResponse.PreCPUStats?.CPUUsage != null)
        {
            var cpuDelta = (double)(statsResponse.CPUStats.CPUUsage.TotalUsage - statsResponse.PreCPUStats.CPUUsage.TotalUsage);
            var systemDelta = (double)(statsResponse.CPUStats.SystemUsage - statsResponse.PreCPUStats.SystemUsage);
            var numCpus = statsResponse.CPUStats.OnlineCPUs > 0
                ? statsResponse.CPUStats.OnlineCPUs
                : (uint)(statsResponse.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
            if (systemDelta > 0 && cpuDelta >= 0)
                cpuPercent = cpuDelta / systemDelta * numCpus * 100.0;
        }

        // Memory
        long memUsage = (long)(statsResponse.MemoryStats?.Usage ?? 0);
        long memLimit = (long)(statsResponse.MemoryStats?.Limit ?? 0);
        double memPercent = memLimit > 0 ? (double)memUsage / memLimit * 100.0 : 0;

        // Disk: use container's SizeRootFs from inspect if available
        long diskUsage = inspect.SizeRootFs ?? 0;
        long diskLimit = 0;

        return new ContainerStats
        {
            CpuPercent = Math.Round(cpuPercent, 1),
            MemoryUsageBytes = memUsage,
            MemoryLimitBytes = memLimit,
            MemoryPercent = Math.Round(memPercent, 1),
            DiskUsageBytes = diskUsage,
            DiskLimitBytes = diskLimit,
            DiskPercent = diskLimit > 0 ? Math.Round((double)diskUsage / diskLimit * 100.0, 1) : 0,
        };
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return await ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var exec = await _client.Exec.ExecCreateContainerAsync(externalId, new ContainerExecCreateParameters
        {
            Cmd = ["sh", "-c", command],
            AttachStdout = true,
            AttachStderr = true
        }, ct);

        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);

        var inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, ct);

        return new ExecResult
        {
            ExitCode = (int)inspect.ExitCode,
            StdOut = stdout,
            StdErr = stderr
        };
    }

    /// <summary>
    /// F4.1 (rivoli-ai/conductor#1934). True streaming exec for Docker:
    /// the same <c>ExecCreate</c> + <c>StartAndAttach</c> surface as the
    /// buffered overload (decision #17 — no new Docker-Engine verb), but
    /// we drain the multiplexed stream chunk-by-chunk, split on newlines,
    /// and hand each complete line to <paramref name="onLine"/> tagged
    /// with its stream kind. The full stdout/stderr is also accumulated so
    /// the returned <see cref="ExecResult"/> matches the buffered shape.
    /// </summary>
    public Task<ExecResult> ExecStreamingAsync(
        string externalId, string command, TimeSpan timeout,
        Action<ExecOutputChunk> onLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return ExecStreamingAsync(
            externalId,
            command,
            timeout,
            (chunk, _) =>
            {
                onLine(chunk);
                return ValueTask.CompletedTask;
            },
            ct);
    }

    public async Task<ExecResult> ExecStreamingAsync(
        string externalId, string command, TimeSpan timeout,
        Func<ExecOutputChunk, CancellationToken, ValueTask> onLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        var exec = await _client.Exec.ExecCreateContainerAsync(externalId, new ContainerExecCreateParameters
        {
            Cmd = ["sh", "-c", command],
            AttachStdout = true,
            AttachStderr = true,
        }, token);

        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, token);

        var stdoutAll = new System.Text.StringBuilder();
        var stderrAll = new System.Text.StringBuilder();
        // Per-stream carry buffers: a chunk may split a line across reads,
        // so we hold the unterminated tail until the next newline arrives.
        var stdoutCarry = new System.Text.StringBuilder();
        var stderrCarry = new System.Text.StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, token);
            if (read.EOF || read.Count == 0)
            {
                break;
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read.Count);
            if (read.Target == MultiplexedStream.TargetStream.StandardError)
            {
                stderrAll.Append(text);
                await EmitLinesAsync(stderrCarry, text, ExecStreamKind.Stderr, onLine, token);
            }
            else
            {
                stdoutAll.Append(text);
                await EmitLinesAsync(stdoutCarry, text, ExecStreamKind.Stdout, onLine, token);
            }
        }

        // Flush any unterminated trailing line (output that never ended in
        // a newline still reaches the live stream).
        await FlushCarryAsync(stdoutCarry, ExecStreamKind.Stdout, onLine, token);
        await FlushCarryAsync(stderrCarry, ExecStreamKind.Stderr, onLine, token);

        var inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, token);
        return new ExecResult
        {
            ExitCode = (int)inspect.ExitCode,
            StdOut = stdoutAll.ToString(),
            StdErr = stderrAll.ToString(),
        };
    }

    // Append `text` to the carry buffer, then emit one callback per
    // complete (newline-terminated) line, leaving any partial tail in the
    // carry for the next read.
    private static async ValueTask EmitLinesAsync(
        System.Text.StringBuilder carry, string text,
        ExecStreamKind kind,
        Func<ExecOutputChunk, CancellationToken, ValueTask> onLine,
        CancellationToken ct)
    {
        carry.Append(text);
        var combined = carry.ToString();
        var normalized = combined.Replace("\r\n", "\n");

        int start = 0;
        int idx;
        while ((idx = normalized.IndexOf('\n', start)) >= 0)
        {
            var line = normalized.Substring(start, idx - start);
            await onLine(new ExecOutputChunk(kind, line), ct);
            start = idx + 1;
        }

        carry.Clear();
        if (start < normalized.Length)
        {
            carry.Append(normalized, start, normalized.Length - start);
        }
    }

    private static async ValueTask FlushCarryAsync(
        System.Text.StringBuilder carry,
        ExecStreamKind kind,
        Func<ExecOutputChunk, CancellationToken, ValueTask> onLine,
        CancellationToken ct)
    {
        if (carry.Length > 0)
        {
            await onLine(new ExecOutputChunk(kind, carry.ToString()), ct);
            carry.Clear();
        }
    }

    /// <summary>
    /// Opens a PTY-backed exec session via Docker's exec API with
    /// <c>Tty=true</c>. The Docker daemon allocates the PTY inside
    /// the container; we own the wire (the multiplexed stream) and
    /// the resize API call. Conductor #875 PR 1.
    ///
    /// Replaces the previous chain
    /// <c>script + docker exec -it + bash</c> with
    /// <c>Docker.DotNet exec API + tty=true</c>. SIGWINCH propagates
    /// because the daemon manages the PTY end-to-end.
    /// </summary>
    public async Task<IInteractiveExecSession?> OpenInteractiveExecAsync(
        string externalId,
        string[] command,
        string user,
        string workingDirectory,
        int cols,
        int rows,
        CancellationToken ct = default)
    {
        var exec = await _client.Exec.ExecCreateContainerAsync(externalId, new ContainerExecCreateParameters
        {
            Cmd = command,
            User = user,
            WorkingDir = workingDirectory,
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = true,
        }, ct);

        // Open a hand-rolled hijacked HTTP attach instead of going
        // through Docker.DotNet's `StartAndAttachContainerExecAsync`.
        // Docker.DotNet 3.125.x's MultiplexedStream wraps a one-way
        // ChunkedReadStream — writes go through the wrapper without
        // ever reaching the daemon, so keystrokes never echo and the
        // terminal looks frozen. Verified independently of Conductor
        // via `pty-test`: every Docker.DotNet attach API fails to
        // deliver writes; raw HTTP/1.1 hijack works. Conductor #875.
        Stream hijackedStream;
        try
        {
            hijackedStream = await OpenHijackedExecAttachAsync(exec.ID, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[PTY-EXEC] OpenHijackedExecAttachAsync failed for exec {ExecId}",
                exec.ID);
            throw;
        }

        // ExecCreateContainerParameters has no size field, so the
        // PTY starts at 80x24. Resize immediately to the renderer's
        // reported size to avoid an initial mismatch flash.
        try
        {
            await _client.Exec.ResizeContainerExecTtyAsync(exec.ID, new ContainerResizeParameters
            {
                Height = (long)rows,
                Width = (long)cols,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[PTY-EXEC] Initial resize to {Cols}x{Rows} failed for exec {ExecId}",
                cols, rows, exec.ID);
        }

        _logger.LogInformation(
            "[PTY-EXEC] opened container={Container} exec={ExecId} cols={Cols} rows={Rows} user={User} cwd={Cwd}",
            externalId, exec.ID, cols, rows, user, workingDirectory);

        return new DockerInteractiveExecSession(_client, exec.ID, hijackedStream, _logger);
    }

    /// <summary>
    /// Opens a bidirectional, hijacked HTTP/1.1 connection to the
    /// Docker daemon's <c>/exec/{id}/start</c> endpoint and returns
    /// the raw upgraded stream. The stream multiplexes raw bytes
    /// (TTY mode — no 8-byte multiplex framing) for both directions.
    ///
    /// Why not <see cref="DockerClient.Exec.StartAndAttachContainerExecAsync"/>?
    /// Docker.DotNet 3.125.x's <c>MultiplexedStream.WriteAsync</c>
    /// writes into a one-way <c>ChunkedReadStream</c> wrapper. The
    /// bytes never reach the daemon — keystrokes are silently
    /// dropped, the kernel never echoes, and the terminal looks
    /// frozen. Hand-rolling the upgrade keeps us bidirectional.
    /// Conductor #875.
    /// </summary>
    private async Task<Stream> OpenHijackedExecAttachAsync(string execId, CancellationToken ct)
    {
        if (!_endpoint.StartsWith("unix://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[PTY-EXEC] Hijacked exec attach only supports unix sockets; endpoint={_endpoint}");
        }
        var socketPath = _endpoint["unix://".Length..];

        var sock = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        // NB: do NOT set sock.NoDelay — it's TCP-only and throws
        // SocketException(45) "Operation not supported" on Unix
        // domain sockets, which would silently fall us back to the
        // legacy script-based path.
        await sock.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), ct);
        var ns = new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);

        var bodyBytes = System.Text.Encoding.UTF8.GetBytes("{\"Detach\":false,\"Tty\":true}");
        var requestHeaders =
            $"POST /v1.41/exec/{execId}/start HTTP/1.1\r\n" +
            "Host: docker\r\n" +
            "Content-Type: application/json\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: tcp\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var requestBytes = System.Text.Encoding.UTF8.GetBytes(requestHeaders);
        await ns.WriteAsync(requestBytes, ct);
        await ns.WriteAsync(bodyBytes, ct);

        var headerBuf = new byte[8192];
        var headerLen = 0;
        var bodyStart = -1;
        while (bodyStart < 0 && headerLen < headerBuf.Length)
        {
            var n = await ns.ReadAsync(headerBuf.AsMemory(headerLen), ct);
            if (n <= 0) throw new IOException("[PTY-EXEC] connection closed before HTTP headers");
            headerLen += n;
            for (int i = 0; i + 3 < headerLen; i++)
            {
                if (headerBuf[i] == '\r' && headerBuf[i + 1] == '\n' &&
                    headerBuf[i + 2] == '\r' && headerBuf[i + 3] == '\n')
                {
                    bodyStart = i + 4;
                    break;
                }
            }
        }
        if (bodyStart < 0)
            throw new IOException("[PTY-EXEC] HTTP headers exceeded 8 KB without a CRLFCRLF");

        var statusLine = System.Text.Encoding.UTF8.GetString(headerBuf, 0, headerLen).Split("\r\n")[0];
        if (!statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            throw new IOException($"[PTY-EXEC] expected 101 Switching Protocols, got: {statusLine}");

        // Bytes after headers (if any) are the start of the upgraded
        // stream and must be replayed before subsequent reads.
        if (bodyStart < headerLen)
        {
            var leftover = new byte[headerLen - bodyStart];
            Array.Copy(headerBuf, bodyStart, leftover, 0, leftover.Length);
            return new PrependBufferStream(leftover, ns);
        }
        return ns;
    }

    /// <summary>
    /// Stream wrapper that replays a leftover prefix on the first
    /// read(s) before delegating to the underlying stream. Used
    /// when we accidentally over-read past the HTTP CRLFCRLF while
    /// looking for the end of headers.
    /// </summary>
    private sealed class PrependBufferStream : Stream
    {
        private readonly byte[] _prefix;
        private int _prefixOffset;
        private readonly Stream _inner;

        public PrependBufferStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _prefixOffset = 0;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var take = Math.Min(count, _prefix.Length - _prefixOffset);
                Array.Copy(_prefix, _prefixOffset, buffer, offset, take);
                _prefixOffset += take;
                return take;
            }
            return _inner.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var take = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                _prefix.AsSpan(_prefixOffset, take).CopyTo(buffer.Span);
                _prefixOffset += take;
                return take;
            }
            return await _inner.ReadAsync(buffer, ct);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Lists every externalId currently known to the Docker daemon
    /// (running OR stopped). Used by the startup reconciler to detect
    /// rows whose containers were removed out-of-band (host reboot,
    /// manual <c>docker rm -f</c>). Conductor #840.
    /// </summary>
    public async Task<HashSet<string>?> ListExternalIdsAsync(CancellationToken ct = default)
    {
        try
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                ct);
            // Docker returns full 64-char IDs in `ID`. Andy stores the
            // same full form (see CreateContainerAsync), so a direct
            // string compare is correct — no truncation needed.
            return new HashSet<string>(containers.Select(c => c.ID), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CONTAINERS-RECONCILE] Docker ListContainers failed");
            return null;
        }
    }

    /// <summary>
    /// Ensures a locally-built fixture image (andy-desktop-*, or the
    /// andy-tasks#390 revision-tagged pre-baked agent image)
    /// exists in the local Docker daemon, building it from the repo's
    /// Dockerfile when missing. Used by the startup warmer so the FIRST
    /// workspace container doesn't pay the image build either.
    /// </summary>
    public async Task EnsureLocalImageAsync(string imageReference, CancellationToken ct)
    {
        if (!Andy.Containers.Validation.LocalImages.IsLocallyBuilt(imageReference))
        {
            throw new ArgumentException(
                $"Image '{imageReference}' is not a supported locally-built image.",
                nameof(imageReference));
        }

        imageReference = Andy.Containers.Validation.LocalImages.IsAgentCli(imageReference)
            ? Andy.Containers.Validation.LocalImages.AgentCli
            : imageReference;

        try
        {
            var existing = await _client.Images.InspectImageAsync(imageReference, ct);
            var actualRevision = existing.Config?.Labels is { } labels
                && labels.TryGetValue("org.opencontainers.image.revision", out var revision)
                ? revision
                : null;
            if (MatchesExpectedLocalImageIdentity(imageReference, actualRevision))
            {
                _logger.LogDebug(
                    "Reusing local image {Image} ({ImageId}); expected revision {ExpectedRevision}, actual {ActualRevision}.",
                    imageReference, existing.ID,
                    Andy.Containers.Validation.LocalImages.IsAgentCli(imageReference)
                        ? Andy.Containers.Validation.LocalImages.AgentCliGitRevision
                        : "tag identity",
                    actualRevision ?? "unlabelled");
                return;
            }

            _logger.LogWarning(
                "Local image {Image} has stale identity (expected revision {ExpectedRevision}, actual {ActualRevision}); rebuilding.",
                imageReference,
                Andy.Containers.Validation.LocalImages.AgentCliGitRevision,
                actualRevision ?? "unlabelled");
        }
        catch (DockerImageNotFoundException)
        {
            // fall through to build
        }

        var buildKey = $"{_endpoint}\n{imageReference}";
        _logger.LogInformation(
            "Local image {Image} is missing on Docker endpoint {Endpoint}; joining single-flight build.",
            imageReference, _endpoint);
        try
        {
            var result = await LocalImageBuildCoordinator.RunAsync(
                buildKey,
                buildCt => BuildLocalImageAsync(imageReference, buildCt),
                ct);
            _logger.LogInformation(
                "Local image {Image} single-flight completed ({Disposition}) after waiting {Elapsed:F1}s.",
                imageReference,
                result.StartedBuild ? "started build" : "joined build",
                result.WaitDuration.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Local image {Image} single-flight failed on Docker endpoint {Endpoint}; a later caller may retry.",
                imageReference, _endpoint);
            throw;
        }
    }

    /// <summary>
    /// Maps a locally-built image reference to its <c>images/&lt;name&gt;</c>
    /// build-context directory name, e.g. <c>andy-agent-cli:3f08f5bb340e</c> →
    /// <c>agent-cli</c> and <c>andy-desktop-python:latest</c> →
    /// <c>desktop-python</c>. Pure so tests can pin the mapping.
    /// </summary>
    internal static string ImageBuildContextName(string imageReference)
    {
        if (Andy.Containers.Validation.LocalImages.IsAgentCli(imageReference))
        {
            return "agent-cli";
        }

        var withoutPrefix = imageReference.StartsWith("andy-", StringComparison.Ordinal)
            ? imageReference["andy-".Length..]
            : imageReference;
        var tagSeparator = withoutPrefix.LastIndexOf(':');
        var lastSlash = withoutPrefix.LastIndexOf('/');
        return tagSeparator > lastSlash ? withoutPrefix[..tagSeparator] : withoutPrefix;
    }

    /// <summary>
    /// Locates the build-context directory for a locally-built image by
    /// probing upward from both the process CWD (dev: repo checkout) and
    /// <see cref="AppContext.BaseDirectory"/> (deployed daemon: the publish
    /// output, which carries <c>images/agent-cli/Dockerfile</c> as content).
    /// </summary>
    internal static string? FindImageBuildDirectory(string contextName)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "images", contextName);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a locally-built fixture image from the repo's Dockerfiles
    /// using the docker CLI.
    /// </summary>
    private async Task BuildLocalImageAsync(string imageReference, CancellationToken ct)
    {
        var imageName = ImageBuildContextName(imageReference);

        var buildDir = FindImageBuildDirectory(imageName);

        if (buildDir == null)
            throw new InvalidOperationException($"Build directory not found for {imageReference}");

        var scriptsDir = Path.Combine(Path.GetDirectoryName(buildDir)!, "..", "scripts", "container");

        _logger.LogInformation("Building local image {Image} from {Dir}", imageReference, buildDir);

        // rivoli-ai/andy-containers#126. Validate the image reference up-
        // front so a malformed value fails with a clear operator-facing
        // error rather than a daemon parse warning. The argv-list path
        // below is the primary defense; this is belt + braces.
        Andy.Containers.Validation.OciReferenceValidator.Validate(imageReference);

        // rivoli-ai/andy-containers#126. ProcessStartInfo.ArgumentList
        // bypasses .NET's Win32-style tokeniser so any whitespace or
        // shell metacharacter that snuck into a template's BaseImage
        // can't smuggle extra docker flags.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in BuildLocalImageArguments(
            imageReference,
            buildDir,
            Directory.Exists(scriptsDir) ? Path.GetFullPath(scriptsDir) : null,
            _endpoint))
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        using var buildTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        buildTimeout.CancelAfter(TimeSpan.FromMinutes(30));
        var buildToken = buildTimeout.Token;

        process.Start();
        // Drain both redirected streams concurrently. Waiting for one stream
        // to close before reading the other can deadlock when BuildKit fills
        // the other pipe during a long build.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(buildToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killEx)
            {
                _logger.LogDebug(killEx, "Could not terminate cancelled local image build for {Image}", imageReference);
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        var outputs = await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = outputs[0];
        var stderr = outputs[1];

        if (process.ExitCode != 0)
        {
            _logger.LogError("Local image build failed: {Stderr}", stderr[..Math.Min(500, stderr.Length)]);
            throw new InvalidOperationException($"Failed to build {imageReference}");
        }

        // `buildx build` can exit zero without exporting its result. `--load`
        // is explicit below, and this inspect is the post-condition that keeps
        // the warmer from claiming an unusable cache-only result is ready.
        try
        {
            var inspect = await _client.Images.InspectImageAsync(imageReference, buildToken);
            var actualRevision = inspect.Config?.Labels is { } labels
                && labels.TryGetValue("org.opencontainers.image.revision", out var revision)
                ? revision
                : null;
            if (!MatchesExpectedLocalImageIdentity(imageReference, actualRevision))
            {
                throw new InvalidOperationException(
                    $"Built image '{imageReference}' has revision label '{actualRevision ?? "<missing>"}', "
                    + $"expected '{Andy.Containers.Validation.LocalImages.AgentCliGitRevision}'.");
            }
            _logger.LogInformation(
                "Local image {Image} built and loaded successfully as {ImageId}",
                imageReference, inspect.ID);
        }
        catch (DockerImageNotFoundException ex)
        {
            _logger.LogError(
                "Buildx exited successfully but image {Image} is not loaded. Stdout tail: {Stdout}",
                imageReference,
                stdout.Length <= 500 ? stdout : stdout[^500..]);
            throw new InvalidOperationException(
                $"Buildx completed but did not load image '{imageReference}' into Docker.", ex);
        }
    }

    /// <summary>Pure argument construction for regression tests.</summary>
    internal static IReadOnlyList<string> BuildLocalImageArguments(
        string imageReference,
        string buildDirectory,
        string? scriptsDirectory,
        string dockerEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerEndpoint);
        var arguments = new List<string>
        {
            // The Docker.DotNet client and Buildx CLI must target the same
            // daemon. Without this global option, Buildx silently uses the
            // operator's default context and --load exports to the wrong
            // daemon for configured TCP/non-default Unix endpoints.
            "--host",
            dockerEndpoint,
            "buildx",
            "build",
            "--load",
            "-t",
            imageReference,
        };

        if (Andy.Containers.Validation.LocalImages.IsAgentCli(imageReference))
        {
            arguments.Add("--build-arg");
            arguments.Add($"ANDY_CLI_GIT_REF={Andy.Containers.Validation.LocalImages.AgentCliGitRevision}");
        }

        if (!string.IsNullOrWhiteSpace(scriptsDirectory))
        {
            arguments.Add("--build-context");
            arguments.Add($"scripts={scriptsDirectory}");
        }

        arguments.Add(buildDirectory);
        return arguments;
    }

    /// <summary>Pure identity decision used by existing-image and post-build validation.</summary>
    internal static bool MatchesExpectedLocalImageIdentity(string imageReference, string? actualRevision) =>
        !Andy.Containers.Validation.LocalImages.IsAgentCli(imageReference)
        || string.Equals(
            actualRevision,
            Andy.Containers.Validation.LocalImages.AgentCliGitRevision,
            StringComparison.Ordinal);
}

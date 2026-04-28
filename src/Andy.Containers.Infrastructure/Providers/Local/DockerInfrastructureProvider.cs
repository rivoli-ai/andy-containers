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
        _logger.LogInformation("Creating Docker container {Name} from {Image}", spec.Name, spec.ImageReference);

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

        // Check if image exists locally first
        bool imageExists = false;
        try
        {
            await _client.Images.InspectImageAsync(spec.ImageReference, ct);
            imageExists = true;
        }
        catch { }

        if (!imageExists)
        {
            // For andy-desktop-* images, build from local Dockerfiles
            if (spec.ImageReference.StartsWith("andy-desktop-"))
            {
                _logger.LogInformation("Building local desktop image {Image}", spec.ImageReference);
                try
                {
                    await BuildDesktopImageAsync(spec.ImageReference, ct);
                    imageExists = true;
                }
                catch (Exception buildEx)
                {
                    _logger.LogWarning(buildEx, "Failed to build desktop image {Image}", spec.ImageReference);
                }
            }

            // Try pulling from registry if not a local build or build failed
            if (!imageExists)
            {
                try
                {
                    await _client.Images.CreateImageAsync(
                        new ImagesCreateParameters { FromImage = spec.ImageReference },
                        null,
                        new Progress<JSONMessage>(m => _logger.LogDebug("Pull: {Status}", m.Status)),
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to pull image {Image}, trying local", spec.ImageReference);
                }
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
            Image = spec.ImageReference,
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
        var inspect = await _client.Containers.InspectContainerAsync(externalId, ct);
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

        return new ConnectionInfo
        {
            IpAddress = inspect.NetworkSettings?.IPAddress,
            PortMappings = ports,
            IdeEndpoint = ports.TryGetValue(8080, out var idePort) ? $"https://localhost:{idePort}" : null,
            VncEndpoint = ports.TryGetValue(6080, out var vncPort) ? $"https://localhost:{vncPort}" : null,
            SshEndpoint = ports.TryGetValue(22, out var sshPort) ? $"ssh root@localhost -p {sshPort}" : null
        };
    }

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
    /// Builds a desktop image from local Dockerfiles using docker CLI.
    /// </summary>
    private async Task BuildDesktopImageAsync(string imageReference, CancellationToken ct)
    {
        var imageName = imageReference.Replace(":latest", "").Replace("andy-", "");

        // Search upward for the images/ directory
        string? buildDir = null;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "images", imageName);
            if (Directory.Exists(candidate)) { buildDir = candidate; break; }
            dir = dir.Parent;
        }

        if (buildDir == null)
            throw new InvalidOperationException($"Build directory not found for {imageReference}");

        var scriptsDir = Path.Combine(Path.GetDirectoryName(buildDir)!, "..", "scripts", "container");

        _logger.LogInformation("Building desktop image {Image} from {Dir}", imageReference, buildDir);

        var args = $"buildx build -t {imageReference}";
        if (Directory.Exists(scriptsDir))
            args += $" --build-context scripts={Path.GetFullPath(scriptsDir)}";
        args += $" {buildDir}";

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        process.Start();
        _ = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError("Desktop image build failed: {Stderr}", stderr[..Math.Min(500, stderr.Length)]);
            throw new InvalidOperationException($"Failed to build {imageReference}");
        }

        _logger.LogInformation("Desktop image {Image} built successfully", imageReference);
    }
}

using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Providers.Apple;

/// <summary>
/// Infrastructure provider for Apple Containers on macOS.
/// Uses the `container` CLI tool for lightweight Linux VMs on Apple Silicon.
/// CLI reference: https://github.com/apple/container
/// </summary>
public class AppleContainerProvider : IInfrastructureProvider
{
    private readonly string _cliPath;
    private readonly ILogger<AppleContainerProvider> _logger;

    public ProviderType Type => ProviderType.AppleContainer;

    public AppleContainerProvider(string? connectionConfig, ILogger<AppleContainerProvider> logger)
    {
        _logger = logger;
        _cliPath = "container";
        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("cliPath", out var path))
                    _cliPath = path.GetString() ?? _cliPath;
            }
            catch { }
        }
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.AppleContainer,
            SupportedArchitectures = ["arm64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = Environment.ProcessorCount,
            MaxMemoryMb = 16384,
            MaxDiskGb = 50,
            SupportsGpu = false,
            SupportsVolumeMount = true,
            SupportsPortForwarding = false,
            SupportsExec = true,
            SupportsStreaming = true,
            SupportsOfflineBuild = true
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // `container list` verifies both the binary AND the system service are available.
            // `container --version` only checks the binary exists.
            var result = await RunCliAsync("list --format json", ct, TimeSpan.FromSeconds(10));
            return result.ExitCode == 0 ? ProviderHealth.Healthy : ProviderHealth.Degraded;
        }
        catch
        {
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating Apple Container {Name} from {Image}", spec.Name, spec.ImageReference);

        var name = spec.Name.ToLowerInvariant().Replace(' ', '-');

        // Remove any existing container with the same name (may be left over
        // from a previous run). #127: pass the name as an argv element so a
        // template-supplied container name with spaces can't tokenise into
        // extra CLI flags.
        try
        {
            var inspect = await RunCliWithArgsAsync(["inspect", name], ct, TimeSpan.FromSeconds(5));
            if (inspect.ExitCode == 0)
            {
                _logger.LogInformation("Removing existing Apple Container {Name} before re-creation", name);
                await RunCliWithArgsAsync(["stop", name], ct, TimeSpan.FromSeconds(15));
                await RunCliWithArgsAsync(["delete", name], ct, TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check/remove existing Apple Container {Name}", name);
        }

        // rivoli-ai/andy-containers#127. The previous implementation built the
        // entire `container run ...` line as a single arguments string. A
        // template's BaseImage with a space (`my-image --rm`), an env value
        // with whitespace, or a Command with embedded flags split into extra
        // CLI tokens at .NET's Win32-style parser, escalating template:write
        // into CLI-flag smuggling. BuildCreateContainerArgs returns a
        // string[] passed straight to ProcessStartInfo.ArgumentList — the OS
        // never sees a shell or a tokeniser for these values.
        var runArgs = BuildCreateContainerArgs(spec, name);

        _logger.LogInformation("Running: {CliPath} {ArgCount} args", _cliPath, runArgs.Length);
        var result = await RunCliWithArgsAsync(runArgs, ct, TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to run Apple Container: {result.StdErr}");

        // Wait briefly for the container to get a network address
        await Task.Delay(2000, ct);

        _logger.LogInformation("Apple Container {Name} created and started", name);

        return new ContainerProvisionResult
        {
            ExternalId = name,
            Status = ContainerStatus.Running,
            ConnectionInfo = await GetConnectionInfoAsync(name, ct)
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        var result = await RunCliWithArgsAsync(["start", externalId], ct, TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start: {result.StdErr}");
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var result = await RunCliWithArgsAsync(["stop", externalId], ct, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to stop: {result.StdErr}");
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        // Stop first (ignore errors — may already be stopped)
        await RunCliWithArgsAsync(["stop", externalId], ct, TimeSpan.FromSeconds(15));

        var result = await RunCliWithArgsAsync(["delete", externalId], ct, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to delete: {result.StdErr}");
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var info = await InspectAsync(externalId, ct);
        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = info.Status == "running" ? ContainerStatus.Running : ContainerStatus.Stopped,
            IpAddress = info.IpAddress
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        // Apple Containers do not support runtime resource changes.
        // Resources are fixed at creation time. Log a warning and return current state.
        _logger.LogWarning("Apple Containers do not support runtime resource resize for {Id}", externalId);
        return Task.FromResult(new ContainerProvisionResult { ExternalId = externalId, Status = ContainerStatus.Running });
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var info = await InspectAsync(externalId, ct);
        return new ConnectionInfo
        {
            IpAddress = info.IpAddress
        };
    }

    public Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct)
    {
        return Task.FromResult(new ContainerStats());
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return await ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        // Use ArgumentList to avoid shell quoting issues with ProcessStartInfo.Arguments.
        // Apple `container exec` syntax: container exec <id> <command> [args...]
        var result = await RunCliWithArgsAsync(["exec", externalId, "sh", "-c", command], ct, timeout);
        return new ExecResult
        {
            ExitCode = result.ExitCode,
            StdOut = result.StdOut,
            StdErr = result.StdErr
        };
    }

    /// <summary>
    /// Lists every externalId currently known to the Apple `container`
    /// runtime via <c>container ls -a</c>. Used by the startup
    /// reconciler to detect rows whose containers were removed
    /// out-of-band (host reboot, manual deletion). Conductor #840.
    /// </summary>
    public async Task<HashSet<string>?> ListExternalIdsAsync(CancellationToken ct = default)
    {
        // `container ls -a --format json` returns an array of records;
        // extract every `id` field and collect into a HashSet.
        var result = await RunCliAsync("ls -a --format json", ct, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "[CONTAINERS-RECONCILE] container ls -a failed (exit {Exit}): {Stderr}",
                result.ExitCode,
                result.StdErr);
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            return ids;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // Apple's CLI uses `configuration.id` per the inspect schema;
                // fall back to top-level `id` for forward-compat.
                if (item.TryGetProperty("configuration", out var config) &&
                    config.TryGetProperty("id", out var configId) &&
                    configId.ValueKind == JsonValueKind.String)
                {
                    var id = configId.GetString();
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
                else if (item.TryGetProperty("id", out var idProp) &&
                         idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString();
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "[CONTAINERS-RECONCILE] Failed to parse container ls JSON output");
            return null;
        }

        return ids;
    }

    private async Task<InspectInfo> InspectAsync(string externalId, CancellationToken ct)
    {
        var result = await RunCliWithArgsAsync(["inspect", externalId], ct, TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            return new InspectInfo { Status = "unknown" };

        try
        {
            // `container inspect` returns a JSON array
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            var item = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;

            var status = item.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";

            // Network address is at networks[0].address (e.g. "192.168.64.3/24")
            string? ipAddress = null;
            if (item.TryGetProperty("networks", out var networks)
                && networks.ValueKind == JsonValueKind.Array
                && networks.GetArrayLength() > 0)
            {
                var net = networks[0];
                if (net.TryGetProperty("address", out var addr))
                {
                    var raw = addr.GetString();
                    // Strip CIDR suffix (e.g. "/24")
                    ipAddress = raw?.Split('/')[0];
                }
            }

            return new InspectInfo { Status = status, IpAddress = ipAddress };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse inspect output for {Id}", externalId);
            return new InspectInfo { Status = "unknown" };
        }
    }

    /// <summary>
    /// Build the argv list for <c>container run -d ...</c>. Pure function
    /// (static + no I/O) so the construction can be unit-tested without
    /// the Apple CLI being installed on the test runner. The returned
    /// array is fed straight to <see cref="ProcessStartInfo.ArgumentList"/>;
    /// each element becomes one argv slot, never tokenised by a shell or
    /// .NET's Win32 parser.
    /// </summary>
    /// <remarks>
    /// rivoli-ai/andy-containers#127. Order matters here: positional
    /// values (image ref, command, command args) must appear in the
    /// run-time positions Apple's CLI expects, but every interpolated
    /// value goes through unescaped. A template with
    /// <c>BaseImage = "evil --rm"</c> would have split into two argv
    /// elements under the previous string-based path; here it stays
    /// as a single (unrecognised, error-out-cleanly) argv slot.
    /// </remarks>
    internal static string[] BuildCreateContainerArgs(ContainerSpec spec, string name)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var args = new List<string> { "run", "--name", name };

        if (spec.Resources is not null)
        {
            if (spec.Resources.CpuCores > 0)
            {
                args.Add("-c");
                args.Add(spec.Resources.CpuCores.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (spec.Resources.MemoryMb > 0)
            {
                args.Add("-m");
                args.Add($"{spec.Resources.MemoryMb}M");
            }
        }

        // Env vars pass as separate `-e KEY=VALUE` argv slots. Whitespace
        // and quote characters in the value are now safe — the previous
        // skip-with-warning workaround is gone with the string-build path.
        if (spec.EnvironmentVariables is { Count: > 0 })
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
            {
                args.Add("-e");
                args.Add($"{key}={value ?? string.Empty}");
            }
        }

        args.Add("-d");
        args.Add(spec.ImageReference);

        if (!string.IsNullOrEmpty(spec.Command))
        {
            args.Add(spec.Command);
            if (spec.Arguments is not null)
            {
                args.AddRange(spec.Arguments);
            }
        }
        else
        {
            // Default keep-alive command. Two argv elements (`sleep` then
            // `infinity`) so even this default doesn't accidentally
            // tokenise differently from caller-supplied commands.
            args.Add("sleep");
            args.Add("infinity");
        }

        return args.ToArray();
    }

    private async Task<CliResult> RunCliAsync(string arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        _logger.LogDebug("Running: {Cli} {Args}", _cliPath, arguments);

        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start {_cliPath}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
            cts.CancelAfter(timeout.Value);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return new CliResult(process.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
    }

    /// <summary>
    /// Runs the CLI with an explicit argument list, avoiding shell quoting issues.
    /// Used for exec where the command string must be passed as a single argument to sh -c.
    /// </summary>
    private async Task<CliResult> RunCliWithArgsAsync(string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        _logger.LogDebug("Running: {Cli} {Args}", _cliPath, string.Join(" ", args));

        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start {_cliPath}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
            cts.CancelAfter(timeout.Value);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return new CliResult(process.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
    }

    private record CliResult(int ExitCode, string? StdOut, string? StdErr);
    private record InspectInfo
    {
        public string Status { get; init; } = "unknown";
        public string? IpAddress { get; init; }
    }
}

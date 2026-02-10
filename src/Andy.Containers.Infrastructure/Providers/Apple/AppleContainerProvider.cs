using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Providers.Apple;

/// <summary>
/// Infrastructure provider for Apple Containers on macOS.
/// Uses the `container` CLI tool for lightweight Linux VMs on Apple Silicon.
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
            var result = await RunCliAsync("--version", ct);
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

        // Initialize a new container from an OCI image
        var initResult = await RunCliAsync($"init --name {name} {spec.ImageReference}", ct);
        if (initResult.ExitCode != 0)
            throw new InvalidOperationException($"Failed to init Apple Container: {initResult.StdErr}");

        // Configure resources
        if (spec.Resources is not null)
        {
            await RunCliAsync($"set {name} --cpus {spec.Resources.CpuCores} --memory {spec.Resources.MemoryMb}m", ct);
        }

        // Configure port forwarding
        if (spec.PortMappings is not null)
        {
            foreach (var (containerPort, hostPort) in spec.PortMappings)
            {
                await RunCliAsync($"set {name} --port {hostPort}:{containerPort}", ct);
            }
        }

        // Start the container
        var startResult = await RunCliAsync($"start {name}", ct);
        if (startResult.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start Apple Container: {startResult.StdErr}");

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
        var result = await RunCliAsync($"start {externalId}", ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start: {result.StdErr}");
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        var result = await RunCliAsync($"stop {externalId}", ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to stop: {result.StdErr}");
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        await RunCliAsync($"stop {externalId}", ct);
        var result = await RunCliAsync($"remove {externalId}", ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to remove: {result.StdErr}");
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var result = await RunCliAsync($"inspect {externalId}", ct);
        var running = result.StdOut?.Contains("running", StringComparison.OrdinalIgnoreCase) ?? false;
        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = running ? ContainerStatus.Running : ContainerStatus.Stopped
        };
    }

    public async Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        await RunCliAsync($"set {externalId} --cpus {resources.CpuCores} --memory {resources.MemoryMb}m", ct);
        return new ContainerProvisionResult { ExternalId = externalId, Status = ContainerStatus.Running };
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var result = await RunCliAsync($"inspect {externalId}", ct);
        // Parse port mappings from inspect output
        return new ConnectionInfo
        {
            IpAddress = "127.0.0.1"
        };
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return await ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        var result = await RunCliAsync($"exec {externalId} -- sh -c \"{command.Replace("\"", "\\\"")}\"", ct, timeout);
        return new ExecResult
        {
            ExitCode = result.ExitCode,
            StdOut = result.StdOut,
            StdErr = result.StdErr
        };
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

        using var cts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
            cts.CancelAfter(timeout.Value);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private record CliResult(int ExitCode, string? StdOut, string? StdErr);
}

using System.Text.Json;
using Amazon;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime;
using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;
using ContainerSpec = Andy.Containers.Abstractions.ContainerSpec;
using ContainerStatus = Andy.Containers.Models.ContainerStatus;
using Task = System.Threading.Tasks.Task;

namespace Andy.Containers.Infrastructure.Providers.Aws;

public class AwsFargateProvider : IInfrastructureProvider
{
    private readonly ILogger<AwsFargateProvider> _logger;
    private readonly AmazonECSClient _ecsClient;
    private readonly string _clusterName;
    private readonly string[] _subnetIds;
    private readonly string[] _securityGroupIds;
    private readonly string _region;

    public ProviderType Type => ProviderType.AwsFargate;

    public AwsFargateProvider(string? connectionConfig, ILogger<AwsFargateProvider> logger)
    {
        _logger = logger;
        _clusterName = "andy-containers";
        _subnetIds = [];
        _securityGroupIds = [];
        _region = "us-east-1";

        string? accessKeyId = null;
        string? secretAccessKey = null;

        if (!string.IsNullOrEmpty(connectionConfig))
        {
            try
            {
                var config = JsonSerializer.Deserialize<JsonElement>(connectionConfig);
                if (config.TryGetProperty("region", out var region))
                    _region = region.GetString() ?? _region;
                if (config.TryGetProperty("accessKeyId", out var aki))
                    accessKeyId = aki.GetString();
                if (config.TryGetProperty("secretAccessKey", out var sak))
                    secretAccessKey = sak.GetString();
                if (config.TryGetProperty("clusterName", out var cn))
                    _clusterName = cn.GetString() ?? _clusterName;
                if (config.TryGetProperty("vpcSubnetIds", out var subnets))
                    _subnetIds = subnets.EnumerateArray().Select(s => s.GetString()!).ToArray();
                if (config.TryGetProperty("securityGroupIds", out var sgs))
                    _securityGroupIds = sgs.EnumerateArray().Select(s => s.GetString()!).ToArray();
            }
            catch { }
        }

        var regionEndpoint = RegionEndpoint.GetBySystemName(_region);
        _ecsClient = !string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey)
            ? new AmazonECSClient(new BasicAWSCredentials(accessKeyId, secretAccessKey), regionEndpoint)
            : new AmazonECSClient(regionEndpoint);
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return System.Threading.Tasks.Task.FromResult(new ProviderCapabilities
        {
            Type = ProviderType.AwsFargate,
            SupportedArchitectures = ["amd64", "arm64"],
            SupportedOperatingSystems = ["linux"],
            MaxCpuCores = 16,
            MaxMemoryMb = 122880,
            MaxDiskGb = 200,
            SupportsGpu = false, // Fargate doesn't support GPU, need EC2 launch type
            SupportsVolumeMount = false,
            SupportsPortForwarding = true,
            SupportsExec = true,  // ECS Exec via SSM
            SupportsStreaming = false,
            SupportsOfflineBuild = false
        });
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            var response = await _ecsClient.DescribeClustersAsync(new DescribeClustersRequest
            {
                Clusters = [_clusterName]
            }, ct);

            var cluster = response.Clusters.FirstOrDefault();
            if (cluster is null || cluster.Status != "ACTIVE")
                return ProviderHealth.Degraded;

            return ProviderHealth.Healthy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AWS Fargate health check failed");
            return ProviderHealth.Unreachable;
        }
    }

    public async Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        _logger.LogInformation("Creating AWS Fargate task {Name} from {Image}", spec.Name, spec.ImageReference);

        var resources = spec.Resources ?? new ResourceSpec();
        var (cpuUnits, memoryMb) = NormalizeFargateResources(resources.CpuCores, resources.MemoryMb);
        var taskFamily = $"andy-{Guid.NewGuid().ToString("N")[..12]}";

        // Register task definition
        var containerDef = new ContainerDefinition
        {
            Name = spec.Name.ToLowerInvariant().Replace(' ', '-'),
            Image = spec.ImageReference,
            Essential = true,
            LinuxParameters = new LinuxParameters { InitProcessEnabled = true } // Required for ECS Exec
        };

        if (spec.PortMappings is not null)
        {
            foreach (var (containerPort, _) in spec.PortMappings)
            {
                containerDef.PortMappings.Add(new PortMapping
                {
                    ContainerPort = containerPort,
                    Protocol = TransportProtocol.Tcp
                });
            }
        }

        if (spec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
            {
                containerDef.Environment.Add(new Amazon.ECS.Model.KeyValuePair { Name = key, Value = value });
            }
        }

        if (!string.IsNullOrEmpty(spec.Command))
        {
            containerDef.Command.Add(spec.Command);
            if (spec.Arguments is not null)
                containerDef.Command.AddRange(spec.Arguments);
        }

        var taskDefResponse = await _ecsClient.RegisterTaskDefinitionAsync(new RegisterTaskDefinitionRequest
        {
            Family = taskFamily,
            NetworkMode = NetworkMode.Awsvpc,
            RequiresCompatibilities = ["FARGATE"],
            Cpu = cpuUnits.ToString(),
            Memory = memoryMb.ToString(),
            ContainerDefinitions = [containerDef]
        }, ct);

        // Run task
        var runResponse = await _ecsClient.RunTaskAsync(new RunTaskRequest
        {
            Cluster = _clusterName,
            TaskDefinition = taskDefResponse.TaskDefinition.TaskDefinitionArn,
            LaunchType = LaunchType.FARGATE,
            Count = 1,
            EnableExecuteCommand = true,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration
                {
                    Subnets = _subnetIds.ToList(),
                    SecurityGroups = _securityGroupIds.ToList(),
                    AssignPublicIp = AssignPublicIp.ENABLED
                }
            }
        }, ct);

        var task = runResponse.Tasks.FirstOrDefault()
            ?? throw new InvalidOperationException("Failed to start Fargate task");

        _logger.LogInformation("AWS Fargate task {Arn} started", task.TaskArn);

        return new ContainerProvisionResult
        {
            ExternalId = task.TaskArn,
            Status = ContainerStatus.Pending // Fargate tasks take time to start
        };
    }

    public async Task StartContainerAsync(string externalId, CancellationToken ct)
    {
        // Fargate tasks can't be restarted — need to run a new task
        // For now, just verify the task exists
        var info = await GetContainerInfoAsync(externalId, ct);
        if (info.Status == ContainerStatus.Running) return;
        throw new NotSupportedException("Fargate tasks cannot be restarted. Create a new container instead.");
    }

    public async Task StopContainerAsync(string externalId, CancellationToken ct)
    {
        await _ecsClient.StopTaskAsync(new StopTaskRequest
        {
            Cluster = _clusterName,
            Task = externalId,
            Reason = "Stopped by Andy Containers"
        }, ct);
        _logger.LogInformation("AWS Fargate task {Arn} stopped", externalId);
    }

    public async Task DestroyContainerAsync(string externalId, CancellationToken ct)
    {
        // Stop the task first
        try
        {
            await StopContainerAsync(externalId, ct);
        }
        catch { /* Task may already be stopped */ }

        _logger.LogInformation("AWS Fargate task {Arn} destroyed", externalId);
    }

    public async Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct)
    {
        var response = await _ecsClient.DescribeTasksAsync(new DescribeTasksRequest
        {
            Cluster = _clusterName,
            Tasks = [externalId]
        }, ct);

        var task = response.Tasks.FirstOrDefault()
            ?? throw new InvalidOperationException($"Task {externalId} not found");

        var status = task.LastStatus switch
        {
            "RUNNING" => ContainerStatus.Running,
            "STOPPED" or "DEPROVISIONING" => ContainerStatus.Stopped,
            _ => ContainerStatus.Pending
        };

        string? ip = null;
        if (task.Attachments is not null)
        {
            var eni = task.Attachments.FirstOrDefault(a => a.Type == "ElasticNetworkInterface");
            var privateIp = eni?.Details.FirstOrDefault(d => d.Name == "privateIPv4Address");
            ip = privateIp?.Value;
        }

        return new ContainerRuntimeInfo
        {
            ExternalId = externalId,
            Status = status,
            StartedAt = task.StartedAt,
            IpAddress = ip
        };
    }

    public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct)
    {
        throw new NotSupportedException("AWS Fargate does not support in-place resize. Create a new task with updated resources.");
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct)
    {
        var info = await GetContainerInfoAsync(externalId, ct);
        return new ConnectionInfo
        {
            IpAddress = info.IpAddress,
            PortMappings = info.PortMappings
        };
    }

    public Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct)
    {
        return ExecAsync(externalId, command, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct)
    {
        // ECS Exec uses SSM to create an interactive session
        var response = await _ecsClient.ExecuteCommandAsync(new ExecuteCommandRequest
        {
            Cluster = _clusterName,
            Task = externalId,
            Container = null, // Uses the first container
            Command = command,
            Interactive = true
        }, ct);

        _logger.LogInformation("ECS Exec session started for task {Task}: {SessionId}",
            externalId, response.Session?.SessionId);

        // ECS Exec returns a Session object for interactive use via SSM.
        // For simple command execution, we return the session info.
        return new ExecResult
        {
            ExitCode = 0,
            StdOut = $"ECS Exec session started: {response.Session?.SessionId}",
            StdErr = null
        };
    }

    /// <summary>
    /// Normalize CPU and memory to valid Fargate combinations.
    /// Fargate requires specific CPU/memory pairs.
    /// </summary>
    private static (int cpuUnits, int memoryMb) NormalizeFargateResources(double cpuCores, int memoryMb)
    {
        // Fargate CPU units: 256 (.25 vCPU), 512, 1024, 2048, 4096, 8192, 16384
        var cpuUnits = cpuCores switch
        {
            <= 0.25 => 256,
            <= 0.5 => 512,
            <= 1 => 1024,
            <= 2 => 2048,
            <= 4 => 4096,
            <= 8 => 8192,
            _ => 16384
        };

        // Each CPU level supports specific memory ranges
        var normalizedMemory = cpuUnits switch
        {
            256 => Math.Clamp(memoryMb, 512, 2048),
            512 => Math.Clamp(memoryMb, 1024, 4096),
            1024 => Math.Clamp(memoryMb, 2048, 8192),
            2048 => Math.Clamp(memoryMb, 4096, 16384),
            4096 => Math.Clamp(memoryMb, 8192, 30720),
            8192 => Math.Clamp(memoryMb, 16384, 61440),
            16384 => Math.Clamp(memoryMb, 32768, 122880),
            _ => memoryMb
        };

        // Round to nearest 1024 MB
        normalizedMemory = ((normalizedMemory + 1023) / 1024) * 1024;

        return (cpuUnits, normalizedMemory);
    }
}

using System.Threading.Channels;

namespace Andy.Containers.Api.Services;

public record ContainerProvisionJob(
    Guid ContainerId,
    Guid ProviderId,
    string ProviderCode,
    string TemplateBaseImage,
    string ContainerName,
    string OwnerId,
    Abstractions.ResourceSpec? Resources,
    Abstractions.GpuSpec? Gpu,
    bool HasGitRepositories = false,
    IReadOnlyList<string>? PostCreateScripts = null,
    Andy.Containers.Models.CodeAssistantConfig? CodeAssistant = null,
    Dictionary<string, string>? EnvironmentVariables = null);

public class ContainerProvisioningQueue
{
    private readonly Channel<ContainerProvisionJob> _channel =
        Channel.CreateUnbounded<ContainerProvisionJob>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

    public ChannelReader<ContainerProvisionJob> Reader => _channel.Reader;

    public async ValueTask EnqueueAsync(ContainerProvisionJob job, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(job, ct);
    }
}

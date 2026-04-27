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
    Dictionary<string, string>? EnvironmentVariables = null,
    string GuiType = "none",
    string ContainerUser = "root",
    string? OwnerEmail = null,
    string? OwnerPreferredUsername = null,
    string? TemplateName = null,
    string? ProviderName = null,
    // X4 (rivoli-ai/andy-containers#93). EnvironmentProfile context for
    // observability — TemplateBaseImage and GuiType are already resolved
    // by the orchestration service when the request bound a profile, so
    // the worker doesn't need to re-evaluate. These fields land in log
    // lines and let downstream events carry the profile correlation.
    Guid? EnvironmentProfileId = null,
    string? EnvironmentKind = null);

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

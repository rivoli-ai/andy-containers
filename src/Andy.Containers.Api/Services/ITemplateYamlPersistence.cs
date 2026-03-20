using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface ITemplateYamlPersistence
{
    Task WriteYamlAsync(ContainerTemplate template, string yaml, CancellationToken ct = default);
    Task<string?> ReadYamlAsync(ContainerTemplate template, CancellationToken ct = default);
    string GetYamlPath(ContainerTemplate template);
}

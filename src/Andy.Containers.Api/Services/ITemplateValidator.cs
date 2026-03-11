using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface ITemplateValidator
{
    Task<TemplateValidationResult> ValidateYamlAsync(string yaml, CancellationToken ct = default);
    Task<ContainerTemplate> ParseYamlToTemplateAsync(string yaml, CancellationToken ct = default);
}

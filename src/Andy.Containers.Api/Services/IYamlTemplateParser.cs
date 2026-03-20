using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IYamlTemplateParser
{
    YamlValidationResult Validate(string yaml);
    ContainerTemplate Parse(string yaml);
}

public class YamlValidationResult
{
    public bool IsValid { get; set; }
    public List<YamlValidationError> Errors { get; set; } = [];
    public List<YamlValidationWarning> Warnings { get; set; } = [];
}

public class YamlValidationError
{
    public required string Field { get; set; }
    public required string Message { get; set; }
    public int? Line { get; set; }
}

public class YamlValidationWarning
{
    public required string Field { get; set; }
    public required string Message { get; set; }
    public int? Line { get; set; }
}

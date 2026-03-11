namespace Andy.Containers.Models;

public class TemplateValidationResult
{
    public bool Valid { get; set; }
    public List<TemplateValidationError> Errors { get; set; } = [];
    public List<TemplateValidationWarning> Warnings { get; set; } = [];
}

public class TemplateValidationError
{
    public required string Field { get; set; }
    public required string Message { get; set; }
    public string Severity { get; set; } = "error";
}

public class TemplateValidationWarning
{
    public required string Field { get; set; }
    public required string Message { get; set; }
}

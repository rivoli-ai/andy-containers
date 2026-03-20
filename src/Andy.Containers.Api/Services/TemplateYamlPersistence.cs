using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Persists validated YAML to disk in config/templates/{scope}/{code}.yaml.
/// Uses atomic writes (write to temp file, then rename) to prevent partial files.
/// The database is the source of truth; YAML files are a convenience for GetDefinition.
/// </summary>
public class TemplateYamlPersistence : ITemplateYamlPersistence
{
    private readonly string _basePath;

    public TemplateYamlPersistence(string basePath)
    {
        _basePath = basePath;
    }

    public async Task WriteYamlAsync(ContainerTemplate template, string yaml, CancellationToken ct = default)
    {
        var path = GetYamlPath(template);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Atomic write: write to temp file then rename
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, yaml, ct);
        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<string?> ReadYamlAsync(ContainerTemplate template, CancellationToken ct = default)
    {
        var path = GetYamlPath(template);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public string GetYamlPath(ContainerTemplate template)
    {
        var scopeDir = template.CatalogScope switch
        {
            CatalogScope.Global => "global",
            CatalogScope.Organization => $"organization/{template.OrganizationId}",
            CatalogScope.Team => $"team/{template.TeamId}",
            CatalogScope.User => $"user/{template.OwnerId}",
            _ => "global"
        };

        return Path.Combine(_basePath, scopeDir, $"{template.Code}.yaml");
    }
}

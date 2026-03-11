using System.Text.RegularExpressions;
using Andy.Containers.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Andy.Containers.Api.Services;

public partial class TemplateYamlValidator : ITemplateValidator
{
    private const int MaxYamlSizeBytes = 1_048_576; // 1MB
    private const int MaxScriptSizeBytes = 1_048_576;
    private const int MaxTags = 20;
    private const int MaxTagLength = 64;
    private const int MaxEnvValueSize = 32_768; // 32KB

    private static readonly HashSet<string> ValidCatalogScopes = ["global", "organization", "team", "user"];
    private static readonly HashSet<string> ValidIdeTypes = ["none", "code-server", "zed", "both"];
    private static readonly HashSet<string> ValidDepTypes = ["compiler", "runtime", "sdk", "tool", "os_package", "library", "extension", "image"];
    private static readonly HashSet<string> ValidEcosystems = ["nuget", "npm", "pip", "apt", "apk", "brew", "cargo", "go"];
    private static readonly HashSet<string> ValidUpdatePolicies = ["security-only", "patch", "minor", "major", "manual"];

    public Task<TemplateValidationResult> ValidateYamlAsync(string yaml, CancellationToken ct = default)
    {
        var result = new TemplateValidationResult { Valid = true };

        if (string.IsNullOrWhiteSpace(yaml))
        {
            result.Valid = false;
            result.Errors.Add(new TemplateValidationError { Field = "yaml", Message = "YAML content is required" });
            return Task.FromResult(result);
        }

        if (yaml.Length > MaxYamlSizeBytes)
        {
            result.Valid = false;
            result.Errors.Add(new TemplateValidationError { Field = "yaml", Message = $"YAML exceeds maximum size of {MaxYamlSizeBytes} bytes" });
            return Task.FromResult(result);
        }

        Dictionary<string, object?>? doc;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            doc = deserializer.Deserialize<Dictionary<string, object?>>(yaml);
        }
        catch (Exception ex)
        {
            result.Valid = false;
            result.Errors.Add(new TemplateValidationError { Field = "yaml", Message = $"Invalid YAML syntax: {ex.Message}" });
            return Task.FromResult(result);
        }

        if (doc is null)
        {
            result.Valid = false;
            result.Errors.Add(new TemplateValidationError { Field = "yaml", Message = "YAML document is empty" });
            return Task.FromResult(result);
        }

        ValidateRequiredFields(doc, result);
        ValidateOptionalFields(doc, result);
        ValidateDependencies(doc, result);
        ValidateEnvironment(doc, result);
        ValidatePorts(doc, result);
        ValidateScripts(doc, result);
        ValidateTags(doc, result);

        result.Valid = result.Errors.Count == 0;
        return Task.FromResult(result);
    }

    public Task<ContainerTemplate> ParseYamlToTemplateAsync(string yaml, CancellationToken ct = default)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var doc = deserializer.Deserialize<Dictionary<string, object?>>(yaml)
            ?? throw new InvalidOperationException("Failed to parse YAML");

        var template = new ContainerTemplate
        {
            Code = GetString(doc, "code") ?? "",
            Name = GetString(doc, "name") ?? "",
            Version = GetString(doc, "version") ?? "",
            BaseImage = GetString(doc, "base_image") ?? "",
            Description = GetString(doc, "description"),
        };

        if (GetString(doc, "catalog_scope") is string scope)
            template.CatalogScope = Enum.Parse<CatalogScope>(scope.Replace("-", ""), ignoreCase: true);

        if (GetString(doc, "ide_type") is string ide)
        {
            var normalized = ide.Replace("-", "").Replace("_", "");
            template.IdeType = normalized.ToLowerInvariant() switch
            {
                "codeserver" => IdeType.CodeServer,
                "zed" => IdeType.Zed,
                "both" => IdeType.Both,
                _ => IdeType.None
            };
        }

        if (doc.TryGetValue("gpu", out var gpuObj) && gpuObj is Dictionary<object, object> gpu)
        {
            if (gpu.TryGetValue("required", out var req)) template.GpuRequired = ConvertBool(req);
            if (gpu.TryGetValue("preferred", out var pref)) template.GpuPreferred = ConvertBool(pref);
        }

        if (doc.TryGetValue("resources", out var resObj) && resObj is Dictionary<object, object> res)
            template.DefaultResources = System.Text.Json.JsonSerializer.Serialize(res);

        if (doc.TryGetValue("environment", out var envObj) && envObj is Dictionary<object, object> env)
            template.EnvironmentVariables = System.Text.Json.JsonSerializer.Serialize(env);

        if (doc.TryGetValue("ports", out var portsObj) && portsObj is Dictionary<object, object> ports)
            template.Ports = System.Text.Json.JsonSerializer.Serialize(ports);

        if (doc.TryGetValue("scripts", out var scriptsObj) && scriptsObj is Dictionary<object, object> scripts)
            template.Scripts = System.Text.Json.JsonSerializer.Serialize(scripts);

        if (doc.TryGetValue("tags", out var tagsObj) && tagsObj is IList<object> tags)
            template.Tags = tags.Select(t => t?.ToString() ?? "").ToArray();

        return Task.FromResult(template);
    }

    private void ValidateRequiredFields(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        // code
        var code = GetString(doc, "code");
        if (string.IsNullOrEmpty(code))
            result.Errors.Add(new TemplateValidationError { Field = "code", Message = "Field 'code' is required" });
        else if (code.Length < 3 || code.Length > 64)
            result.Errors.Add(new TemplateValidationError { Field = "code", Message = "Field 'code' must be 3-64 characters" });
        else if (!CodeRegex().IsMatch(code))
            result.Errors.Add(new TemplateValidationError { Field = "code", Message = "Field 'code' must be lowercase alphanumeric with hyphens" });

        // name
        var name = GetString(doc, "name");
        if (string.IsNullOrEmpty(name))
            result.Errors.Add(new TemplateValidationError { Field = "name", Message = "Field 'name' is required" });
        else if (name.Length > 128)
            result.Errors.Add(new TemplateValidationError { Field = "name", Message = "Field 'name' must be 1-128 characters" });

        // version
        var version = GetString(doc, "version");
        if (string.IsNullOrEmpty(version))
            result.Errors.Add(new TemplateValidationError { Field = "version", Message = "Field 'version' is required" });
        else if (!SemverRegex().IsMatch(version))
            result.Errors.Add(new TemplateValidationError { Field = "version", Message = "Field 'version' must be valid semver (e.g., 1.0.0)" });

        // base_image
        var baseImage = GetString(doc, "base_image");
        if (string.IsNullOrEmpty(baseImage))
            result.Errors.Add(new TemplateValidationError { Field = "base_image", Message = "Field 'base_image' is required" });
    }

    private void ValidateOptionalFields(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        if (GetString(doc, "catalog_scope") is string scope && !ValidCatalogScopes.Contains(scope))
            result.Errors.Add(new TemplateValidationError { Field = "catalog_scope", Message = $"Invalid catalog_scope: '{scope}'. Must be one of: {string.Join(", ", ValidCatalogScopes)}" });

        if (GetString(doc, "ide_type") is string ide && !ValidIdeTypes.Contains(ide))
            result.Errors.Add(new TemplateValidationError { Field = "ide_type", Message = $"Invalid ide_type: '{ide}'. Must be one of: {string.Join(", ", ValidIdeTypes)}" });

        if (doc.TryGetValue("resources", out var resObj) && resObj is Dictionary<object, object> res)
        {
            ValidateIntRange(res, "cpu_cores", 1, 128, "resources.cpu_cores", result);
            ValidateIntRange(res, "memory_mb", 512, 524288, "resources.memory_mb", result);
            ValidateIntRange(res, "disk_gb", 1, 2048, "resources.disk_gb", result);
        }

        // Warn if code-server/both but port 8080 not declared
        if (GetString(doc, "ide_type") is "code-server" or "both")
        {
            bool has8080 = false;
            if (doc.TryGetValue("ports", out var portsObj) && portsObj is Dictionary<object, object> ports)
                has8080 = ports.Keys.Any(k => k?.ToString() == "8080");
            if (!has8080)
                result.Warnings.Add(new TemplateValidationWarning { Field = "ports", Message = "ide_type requires code-server but port 8080 is not declared" });
        }
    }

    private void ValidateDependencies(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        if (!doc.TryGetValue("dependencies", out var depsObj) || depsObj is not IList<object> deps) return;

        var names = new HashSet<string>();
        for (int i = 0; i < deps.Count; i++)
        {
            if (deps[i] is not Dictionary<object, object> dep) continue;
            var prefix = $"dependencies[{i}]";

            var type = dep.TryGetValue("type", out var t) ? t?.ToString() : null;
            if (string.IsNullOrEmpty(type))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.type", Message = "Dependency 'type' is required" });
            else if (!ValidDepTypes.Contains(type))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.type", Message = $"Invalid dependency type: '{type}'" });

            var name = dep.TryGetValue("name", out var n) ? n?.ToString() : null;
            if (string.IsNullOrEmpty(name))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.name", Message = "Dependency 'name' is required" });
            else if (name.Length > 128)
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.name", Message = "Dependency name must be 1-128 characters" });
            else if (!names.Add(name))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.name", Message = $"Duplicate dependency name: '{name}'" });

            var version = dep.TryGetValue("version", out var v) ? v?.ToString() : null;
            if (string.IsNullOrEmpty(version))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.version", Message = "Dependency 'version' is required" });
            else if (!VersionConstraintParser.IsValid(version))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.version", Message = $"Invalid version constraint: '{version}'" });

            if (dep.TryGetValue("ecosystem", out var eco) && eco is string ecoStr && !ValidEcosystems.Contains(ecoStr))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.ecosystem", Message = $"Invalid ecosystem: '{ecoStr}'" });

            if (dep.TryGetValue("update_policy", out var pol) && pol is string polStr && !ValidUpdatePolicies.Contains(polStr))
                result.Errors.Add(new TemplateValidationError { Field = $"{prefix}.update_policy", Message = $"Invalid update_policy: '{polStr}'" });
        }
    }

    private void ValidateEnvironment(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        if (!doc.TryGetValue("environment", out var envObj) || envObj is not Dictionary<object, object> env) return;

        foreach (var kvp in env)
        {
            var key = kvp.Key?.ToString() ?? "";
            if (!EnvKeyRegex().IsMatch(key))
                result.Errors.Add(new TemplateValidationError { Field = $"environment.{key}", Message = $"Environment variable key '{key}' must match [A-Z_][A-Z0-9_]*" });

            var value = kvp.Value?.ToString() ?? "";
            if (value.Length > MaxEnvValueSize)
                result.Errors.Add(new TemplateValidationError { Field = $"environment.{key}", Message = $"Environment variable value exceeds {MaxEnvValueSize} bytes" });
        }
    }

    private void ValidatePorts(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        if (!doc.TryGetValue("ports", out var portsObj) || portsObj is not Dictionary<object, object> ports) return;

        var seenPorts = new HashSet<int>();
        foreach (var kvp in ports)
        {
            var portStr = kvp.Key?.ToString() ?? "";
            if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
            {
                result.Errors.Add(new TemplateValidationError { Field = $"ports.{portStr}", Message = $"Port must be an integer between 1 and 65535" });
                continue;
            }
            if (!seenPorts.Add(port))
                result.Errors.Add(new TemplateValidationError { Field = $"ports.{portStr}", Message = $"Duplicate port: {port}" });
        }
    }

    private void ValidateScripts(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        if (!doc.TryGetValue("scripts", out var scriptsObj) || scriptsObj is not Dictionary<object, object> scripts) return;

        foreach (var kvp in scripts)
        {
            var name = kvp.Key?.ToString() ?? "";
            var value = kvp.Value?.ToString() ?? "";
            if (value.Length > MaxScriptSizeBytes)
                result.Errors.Add(new TemplateValidationError { Field = $"scripts.{name}", Message = $"Script exceeds maximum size of {MaxScriptSizeBytes} bytes" });
        }
    }

    private void ValidateTags(Dictionary<string, object?> doc, TemplateValidationResult result)
    {
        if (!doc.TryGetValue("tags", out var tagsObj) || tagsObj is not IList<object> tags) return;

        if (tags.Count > MaxTags)
            result.Errors.Add(new TemplateValidationError { Field = "tags", Message = $"Maximum {MaxTags} tags allowed" });

        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i]?.ToString() ?? "";
            if (tag.Length < 1 || tag.Length > MaxTagLength)
                result.Errors.Add(new TemplateValidationError { Field = $"tags[{i}]", Message = $"Tag must be 1-{MaxTagLength} characters" });
        }
    }

    private static void ValidateIntRange(Dictionary<object, object> dict, string key, int min, int max, string field, TemplateValidationResult result)
    {
        if (!dict.TryGetValue(key, out var val)) return;
        if (!int.TryParse(val?.ToString(), out var intVal) || intVal < min || intVal > max)
            result.Errors.Add(new TemplateValidationError { Field = field, Message = $"Must be between {min} and {max}" });
    }

    private static string? GetString(Dictionary<string, object?> doc, string key) =>
        doc.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static bool ConvertBool(object? val) =>
        val is bool b ? b : bool.TryParse(val?.ToString(), out var parsed) && parsed;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*[a-z0-9]$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$")]
    private static partial Regex SemverRegex();

    [GeneratedRegex(@"^[A-Z_][A-Z0-9_]*$")]
    private static partial Regex EnvKeyRegex();
}

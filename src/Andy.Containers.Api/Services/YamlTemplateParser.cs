using System.Text.Json;
using System.Text.RegularExpressions;
using Andy.Containers.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Andy.Containers.Api.Services;

public class YamlTemplateParser : IYamlTemplateParser
{
    private static readonly HashSet<string> KnownTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "name", "description", "version", "base_image",
        "scope", "catalog_scope", "ide_type", "gpu_required", "gpu_preferred",
        "tags", "ports", "environment", "scripts", "resources",
        "dependencies", "git_repositories", "code_assistant"
    };

    private static readonly HashSet<string> ValidDependencyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sdk", "runtime", "compiler", "tool", "library"
    };

    private static readonly Regex CodePattern = new(@"^[a-z][a-z0-9\-]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex SemverPattern = new(@"^\d+\.\d+\.\d+(-[a-zA-Z0-9\.\-]+)?$", RegexOptions.Compiled);

    public YamlValidationResult Validate(string yaml)
    {
        var result = new YamlValidationResult();

        // Parse YAML
        Dictionary<object, object>? dict;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            dict = deserializer.Deserialize<Dictionary<object, object>>(yaml);
        }
        catch (YamlException ex)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "yaml",
                Message = $"Invalid YAML syntax: {ex.Message}",
                Line = (int)ex.Start.Line
            });
            result.IsValid = false;
            return result;
        }

        if (dict is null)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "yaml",
                Message = "YAML content is empty"
            });
            result.IsValid = false;
            return result;
        }

        // Check required fields
        ValidateRequired(dict, "code", result);
        ValidateRequired(dict, "name", result);
        ValidateRequired(dict, "version", result);
        ValidateRequired(dict, "base_image", result);

        // Validate code format
        if (TryGetString(dict, "code", out var code))
        {
            if (!CodePattern.IsMatch(code))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = "code",
                    Message = "Code must be 2-64 characters, lowercase letters, digits, and hyphens only, starting with a letter"
                });
            }
        }

        // Validate version
        if (TryGetString(dict, "version", out var version))
        {
            if (!SemverPattern.IsMatch(version))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = "version",
                    Message = "Version must be valid semver (e.g., 1.0.0 or 1.0.0-alpha)"
                });
            }
        }

        // Validate base_image
        if (TryGetString(dict, "base_image", out var baseImage))
        {
            if (string.IsNullOrWhiteSpace(baseImage) || baseImage.Contains(' ') ||
                (!baseImage.Contains(':') && !baseImage.Contains('@')))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = "base_image",
                    Message = "Base image must be a valid OCI reference (must contain ':' or '@', no spaces)"
                });
            }
        }

        // Validate dependencies
        if (dict.TryGetValue("dependencies", out var depsObj) && depsObj is List<object> deps)
        {
            for (var i = 0; i < deps.Count; i++)
            {
                if (deps[i] is Dictionary<object, object> dep)
                {
                    if (dep.TryGetValue("type", out var typeObj))
                    {
                        var typeStr = typeObj?.ToString() ?? "";
                        if (!ValidDependencyTypes.Contains(typeStr))
                        {
                            result.Errors.Add(new YamlValidationError
                            {
                                Field = $"dependencies[{i}].type",
                                Message = $"Invalid dependency type '{typeStr}'. Must be one of: sdk, runtime, compiler, tool, library"
                            });
                        }
                    }

                    if (!dep.TryGetValue("version", out var verObj) || string.IsNullOrWhiteSpace(verObj?.ToString()))
                    {
                        result.Errors.Add(new YamlValidationError
                        {
                            Field = $"dependencies[{i}].version",
                            Message = "Dependency version is required and must be non-empty"
                        });
                    }
                }
            }
        }

        // Validate scope / catalog_scope
        ValidateEnum<CatalogScope>(dict, "scope", result);
        ValidateEnum<CatalogScope>(dict, "catalog_scope", result);

        // Validate ide_type
        ValidateEnum<IdeType>(dict, "ide_type", result);

        // Validate ports
        if (dict.TryGetValue("ports", out var portsObj))
        {
            IEnumerable<object>? portsList = portsObj switch
            {
                List<object> list => list,
                Dictionary<object, object> portDict => portDict.Values,
                _ => null
            };

            if (portsList is not null)
            {
                foreach (var portItem in portsList)
                {
                    if (int.TryParse(portItem?.ToString(), out var port))
                    {
                        if (port < 1 || port > 65535)
                        {
                            result.Errors.Add(new YamlValidationError
                            {
                                Field = "ports",
                                Message = $"Port {port} is out of range (1-65535)"
                            });
                        }
                    }
                }
            }
        }

        // Unknown top-level keys -> warnings
        foreach (var key in dict.Keys)
        {
            var keyStr = key.ToString()!;
            if (!KnownTopLevelKeys.Contains(keyStr))
            {
                result.Warnings.Add(new YamlValidationWarning
                {
                    Field = keyStr,
                    Message = $"Unknown top-level key '{keyStr}'"
                });
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    public ContainerTemplate Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var dict = deserializer.Deserialize<Dictionary<object, object>>(yaml);

        var template = new ContainerTemplate
        {
            Code = GetString(dict, "code"),
            Name = GetString(dict, "name"),
            Description = GetStringOrNull(dict, "description"),
            Version = GetString(dict, "version"),
            BaseImage = GetString(dict, "base_image")
        };

        // scope / catalog_scope
        if (TryGetString(dict, "scope", out var scope))
            template.CatalogScope = Enum.Parse<CatalogScope>(scope, ignoreCase: true);
        else if (TryGetString(dict, "catalog_scope", out var catalogScope))
            template.CatalogScope = Enum.Parse<CatalogScope>(catalogScope, ignoreCase: true);

        // ide_type
        if (TryGetString(dict, "ide_type", out var ideType))
            template.IdeType = Enum.Parse<IdeType>(ideType, ignoreCase: true);

        // booleans
        if (dict.TryGetValue("gpu_required", out var gpuReq))
            template.GpuRequired = bool.TryParse(gpuReq?.ToString(), out var gr) && gr;
        if (dict.TryGetValue("gpu_preferred", out var gpuPref))
            template.GpuPreferred = bool.TryParse(gpuPref?.ToString(), out var gp) && gp;

        // tags
        if (dict.TryGetValue("tags", out var tagsObj) && tagsObj is List<object> tagsList)
            template.Tags = tagsList.Select(t => t.ToString()!).ToArray();

        // JSON serialized fields
        template.Ports = SerializeIfPresent(dict, "ports");
        template.EnvironmentVariables = SerializeIfPresent(dict, "environment");
        template.Scripts = SerializeIfPresent(dict, "scripts");
        template.DefaultResources = SerializeIfPresent(dict, "resources");
        template.Toolchains = SerializeIfPresent(dict, "dependencies");
        template.GitRepositories = SerializeIfPresent(dict, "git_repositories");
        template.CodeAssistant = SerializeIfPresent(dict, "code_assistant");

        return template;
    }

    private static void ValidateRequired(Dictionary<object, object> dict, string field, YamlValidationResult result)
    {
        if (!dict.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = field,
                Message = $"'{field}' is required"
            });
        }
    }

    private static void ValidateEnum<T>(Dictionary<object, object> dict, string field, YamlValidationResult result)
        where T : struct, Enum
    {
        if (TryGetString(dict, field, out var value))
        {
            if (!Enum.TryParse<T>(value, ignoreCase: true, out _))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = field,
                    Message = $"Invalid value '{value}' for '{field}'. Valid values: {string.Join(", ", Enum.GetNames<T>())}"
                });
            }
        }
    }

    private static bool TryGetString(Dictionary<object, object> dict, string key, out string value)
    {
        if (dict.TryGetValue(key, out var obj) && obj is not null)
        {
            value = obj.ToString()!;
            return !string.IsNullOrWhiteSpace(value);
        }
        value = "";
        return false;
    }

    private static string GetString(Dictionary<object, object> dict, string key)
    {
        return dict.TryGetValue(key, out var obj) ? obj?.ToString() ?? "" : "";
    }

    private static string? GetStringOrNull(Dictionary<object, object> dict, string key)
    {
        return dict.TryGetValue(key, out var obj) ? obj?.ToString() : null;
    }

    private static string? SerializeIfPresent(Dictionary<object, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is null)
            return null;
        return JsonSerializer.Serialize(value);
    }
}

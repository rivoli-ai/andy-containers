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
        "dependencies", "git_repositories", "code_assistant",
        // IM4 (rivoli-ai/andy-containers#253). M1.9 imperative-style fields.
        "extends", "from", "packages", "files", "install", "entrypoint", "markers"
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

        // IM4 (#253). base_image, from (deprecated alias), and extends
        // form a tri-choice: a template needs at least one of them, and
        // base_image / from cannot be supplied together (ambiguous).
        // extends supplies the base image transitively from the parent
        // template, so it satisfies the requirement on its own.
        var hasBaseImage = TryGetString(dict, "base_image", out var baseImage);
        var hasFrom = TryGetString(dict, "from", out var fromAlias);
        var hasExtends = TryGetString(dict, "extends", out var extendsCode);

        if (!hasBaseImage && !hasFrom && !hasExtends)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "base_image",
                Message = "Templates must declare a base image — set 'base_image:' (preferred), 'from:' (deprecated alias), or 'extends:' (inherit from a parent template)."
            });
        }

        if (hasBaseImage && hasFrom)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "from",
                Message = "Specify only one of 'base_image:' (preferred) or 'from:' (deprecated alias) — having both is ambiguous."
            });
        }
        else if (hasFrom)
        {
            // 'from' is accepted only for backward-compat with the
            // M1.9 issue body that triggered Epic IM. Surface a single
            // deprecation warning per parse so callers migrate.
            result.Warnings.Add(new YamlValidationWarning
            {
                Field = "from",
                Message = "'from:' is a deprecated alias for 'base_image:'. Migrate to 'base_image:' — 'from:' will be removed in a future release."
            });

            // Treat 'from' as base_image for the rest of validation.
            baseImage = fromAlias;
            hasBaseImage = true;
        }

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

        // Validate base_image / from format (whichever was supplied)
        if (hasBaseImage)
        {
            if (string.IsNullOrWhiteSpace(baseImage) || baseImage.Contains(' ') ||
                (!baseImage.Contains(':') && !baseImage.Contains('@')))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = hasFrom ? "from" : "base_image",
                    Message = "Base image must be a valid OCI reference (must contain ':' or '@', no spaces)"
                });
            }
        }

        // IM4 (#253). 'extends' must be a valid template code; the
        // actual existence-check + cycle-detection happens at
        // register-time (TemplateExtendsCycleDetector) since the
        // parser doesn't have access to the templates table.
        if (hasExtends && !CodePattern.IsMatch(extendsCode))
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "extends",
                Message = "'extends:' must reference a valid template code (2-64 chars, lowercase letters / digits / hyphens, starting with a letter)."
            });
        }

        // Validate imperative fields. Each is optional; when present,
        // the shape must match what the build backend will consume.
        ValidatePackages(dict, result);
        ValidateFiles(dict, result);
        ValidateInstall(dict, result);
        ValidateEntrypoint(dict, result);
        ValidateMarkers(dict, result);

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

        // IM4 (#253). 'from:' is a deprecated alias for base_image.
        // Validate() emits the warning; Parse() just normalises so
        // downstream code only sees BaseImage. If both are supplied,
        // Validate() rejects it; here we prefer base_image when both
        // somehow slipped past validation so the more authoritative
        // field wins.
        var baseImage = GetString(dict, "base_image");
        if (string.IsNullOrEmpty(baseImage))
        {
            baseImage = GetString(dict, "from");
        }

        var template = new ContainerTemplate
        {
            Code = GetString(dict, "code"),
            Name = GetString(dict, "name"),
            Description = GetStringOrNull(dict, "description"),
            Version = GetString(dict, "version"),
            // BaseImage stays required on the model. When the template
            // only declares 'extends:' and no base, the post-register
            // pipeline resolves it from the parent template before
            // hitting the build backend; the in-memory ContainerTemplate
            // returned here will have an empty string until then.
            BaseImage = baseImage
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

        // IM4 (#253). M1.9 imperative-style fields. These are persisted
        // on the template so the build backend (IM7+) can replay them
        // deterministically at build time.
        template.Extends = GetStringOrNull(dict, "extends");
        template.EntryPoint = GetStringOrNull(dict, "entrypoint");
        template.Packages = SerializeIfPresent(dict, "packages");
        template.Files = SerializeIfPresent(dict, "files");
        template.Install = SerializeIfPresent(dict, "install");
        template.Markers = SerializeIfPresent(dict, "markers");

        return template;
    }

    // IM4 validators for the imperative fields. Kept private and
    // narrow — each method validates one section against the shape
    // the build backend will consume, with field paths in errors so
    // a typo inside files[2].mode is easy to find.

    private static void ValidatePackages(Dictionary<object, object> dict, YamlValidationResult result)
    {
        if (!dict.TryGetValue("packages", out var obj) || obj is null)
        {
            return;
        }

        if (obj is not List<object> list)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "packages",
                Message = "'packages:' must be a list of OS package names (strings)."
            });
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i]?.ToString();
            if (string.IsNullOrWhiteSpace(item))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = $"packages[{i}]",
                    Message = "Package name must be a non-empty string."
                });
            }
        }
    }

    private static void ValidateFiles(Dictionary<object, object> dict, YamlValidationResult result)
    {
        if (!dict.TryGetValue("files", out var obj) || obj is null)
        {
            return;
        }

        if (obj is not List<object> list)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "files",
                Message = "'files:' must be a list of objects, each with 'source', 'dest', and optional 'mode'."
            });
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not Dictionary<object, object> entry)
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = $"files[{i}]",
                    Message = "Each entry in 'files:' must be an object with 'source' and 'dest' fields."
                });
                continue;
            }

            if (!TryGetString(entry, "source", out _))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = $"files[{i}].source",
                    Message = "'source' is required — the multipart-upload logical name of the file to copy."
                });
            }

            if (!TryGetString(entry, "dest", out var dest))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = $"files[{i}].dest",
                    Message = "'dest' is required — the absolute path inside the container."
                });
            }
            else if (!dest.StartsWith('/'))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = $"files[{i}].dest",
                    Message = $"'dest' must be an absolute path (got '{dest}')."
                });
            }

            // mode is optional. Accept octal literals like 0755 or
            // bare integers; reject negatives and impossibly large
            // values. Maximum is 4095 == 07777 octal — covers all
            // standard Unix permission bits including setuid/setgid/sticky.
            const int maxOctalMode = 4095; // 07777
            if (entry.TryGetValue("mode", out var modeObj) && modeObj is not null)
            {
                if (!TryParseOctalMode(modeObj, out var mode) || mode < 0 || mode > maxOctalMode)
                {
                    result.Errors.Add(new YamlValidationError
                    {
                        Field = $"files[{i}].mode",
                        Message = $"'mode' must be a Unix permission octal in [0, 07777] (got '{modeObj}')."
                    });
                }
            }
        }
    }

    private static void ValidateInstall(Dictionary<object, object> dict, YamlValidationResult result)
    {
        if (!dict.TryGetValue("install", out var obj) || obj is null)
        {
            return;
        }

        if (obj is not List<object> list)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "install",
                Message = "'install:' must be a list of shell command strings."
            });
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i]?.ToString();
            if (string.IsNullOrWhiteSpace(item))
            {
                result.Errors.Add(new YamlValidationError
                {
                    Field = $"install[{i}]",
                    Message = "Install command must be a non-empty string."
                });
            }
        }
    }

    private static void ValidateEntrypoint(Dictionary<object, object> dict, YamlValidationResult result)
    {
        if (!dict.TryGetValue("entrypoint", out var obj) || obj is null)
        {
            return;
        }

        // Accept a single string for now. The OCI image-spec also
        // allows a list-of-strings form; if a future spec needs that,
        // generalise here.
        if (obj is List<object>)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "entrypoint",
                Message = "'entrypoint:' must be a single string in IM4. List-of-strings form is not yet supported."
            });
            return;
        }

        var entrypoint = obj.ToString();
        if (string.IsNullOrWhiteSpace(entrypoint))
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "entrypoint",
                Message = "'entrypoint:' must be a non-empty string."
            });
        }
    }

    private static void ValidateMarkers(Dictionary<object, object> dict, YamlValidationResult result)
    {
        if (!dict.TryGetValue("markers", out var obj) || obj is null)
        {
            return;
        }

        // markers is intentionally free-form — caller-defined metadata
        // surfaced through GET /api/images. We require it to be an
        // object (not a list / scalar) so the field paths are stable.
        if (obj is not Dictionary<object, object>)
        {
            result.Errors.Add(new YamlValidationError
            {
                Field = "markers",
                Message = "'markers:' must be an object of key/value pairs (free-form metadata)."
            });
        }
    }

    private static bool TryParseOctalMode(object value, out int mode)
    {
        // YamlDotNet yields ints as int / long, and YAML's 0755 syntax
        // parses straight to decimal in newer parsers — both are fine
        // since the caller validates the range. String forms like
        // "0755" are common in YAML where the user wrote a quoted
        // permission literal — interpret those as octal explicitly.
        switch (value)
        {
            case int i:
                mode = i;
                return true;
            case long l:
                mode = (int)l;
                return true;
            case string s when s.Length > 1 && s.StartsWith('0'):
                return TryParseOctalString(s, out mode);
            case string s:
                return int.TryParse(s, out mode);
            default:
                mode = 0;
                return false;
        }
    }

    private static bool TryParseOctalString(string s, out int mode)
    {
        mode = 0;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '7')
            {
                mode = 0;
                return false;
            }
            mode = (mode << 3) | (ch - '0');
        }
        return true;
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

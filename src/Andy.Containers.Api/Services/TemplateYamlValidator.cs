using System.Text.Json;
using Andy.Containers.Models;
using YamlDotNet.RepresentationModel;

namespace Andy.Containers.Api.Services;

public class TemplateValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<ValidationError> Errors { get; set; } = [];
    public List<ValidationWarning> Warnings { get; set; } = [];
}

public record ValidationError(string Field, string Message);
public record ValidationWarning(string Field, string Message);

public interface ITemplateYamlValidator
{
    Task<TemplateValidationResult> ValidateYamlAsync(string yaml, CancellationToken ct = default);
    Task<ContainerTemplate> ParseYamlToTemplateAsync(string yaml, CancellationToken ct = default);
}

public class TemplateYamlValidator : ITemplateYamlValidator
{
    private static readonly HashSet<string> ValidAuthMethods = ["public_key", "password"];

    public Task<TemplateValidationResult> ValidateYamlAsync(string yaml, CancellationToken ct = default)
    {
        var result = new TemplateValidationResult();

        YamlMappingNode root;
        try
        {
            var yamlStream = new YamlStream();
            yamlStream.Load(new StringReader(yaml));
            root = (YamlMappingNode)yamlStream.Documents[0].RootNode;
        }
        catch (Exception ex)
        {
            result.Errors.Add(new ValidationError("yaml", $"Invalid YAML: {ex.Message}"));
            return Task.FromResult(result);
        }

        // Validate required fields
        if (!root.Children.ContainsKey("code"))
            result.Errors.Add(new ValidationError("code", "Required field 'code' is missing"));
        if (!root.Children.ContainsKey("version"))
            result.Errors.Add(new ValidationError("version", "Required field 'version' is missing"));
        if (!root.Children.ContainsKey("base_image"))
            result.Errors.Add(new ValidationError("base_image", "Required field 'base_image' is missing"));

        // Collect declared ports for SSH conflict check
        var declaredPorts = new HashSet<int>();
        if (root.Children.TryGetValue("ports", out var portsNode) && portsNode is YamlMappingNode portMap)
        {
            foreach (var (portKey, _) in portMap.Children)
            {
                if (int.TryParse(portKey.ToString(), out var port))
                    declaredPorts.Add(port);
            }
        }

        // Validate SSH section
        if (root.Children.TryGetValue("ssh", out var sshNode) && sshNode is YamlMappingNode sshMap)
        {
            var enabled = GetBool(sshMap, "enabled");

            if (enabled)
            {
                // Validate port
                if (sshMap.Children.TryGetValue("port", out var portNode))
                {
                    if (int.TryParse(portNode.ToString(), out var sshPort))
                    {
                        if (sshPort < 1 || sshPort > 65535)
                            result.Errors.Add(new ValidationError("ssh.port", "SSH port must be between 1 and 65535"));
                        if (declaredPorts.Contains(sshPort))
                            result.Errors.Add(new ValidationError("ssh.port", $"SSH port {sshPort} conflicts with a declared port"));
                    }
                    else
                    {
                        result.Errors.Add(new ValidationError("ssh.port", "SSH port must be an integer"));
                    }
                }

                // Validate auth_methods
                if (sshMap.Children.TryGetValue("auth_methods", out var authNode))
                {
                    if (authNode is YamlSequenceNode authSeq)
                    {
                        if (authSeq.Children.Count == 0)
                        {
                            result.Errors.Add(new ValidationError("ssh.auth_methods", "At least one auth method is required when SSH is enabled"));
                        }
                        else
                        {
                            foreach (var method in authSeq.Children)
                            {
                                var value = method.ToString();
                                if (!ValidAuthMethods.Contains(value))
                                    result.Errors.Add(new ValidationError("ssh.auth_methods", $"Invalid auth method '{value}'. Allowed: public_key, password"));
                            }

                            // Warning for password auth
                            if (authSeq.Children.Any(m => m.ToString() == "password"))
                                result.Warnings.Add(new ValidationWarning("ssh.auth_methods", "Password authentication is discouraged; prefer public_key only"));
                        }
                    }
                    else
                    {
                        result.Errors.Add(new ValidationError("ssh.auth_methods", "auth_methods must be an array"));
                    }
                }
                else
                {
                    result.Errors.Add(new ValidationError("ssh.auth_methods", "At least one auth method is required when SSH is enabled"));
                }

                // Validate idle_timeout_minutes
                if (sshMap.Children.TryGetValue("idle_timeout_minutes", out var timeoutNode))
                {
                    if (int.TryParse(timeoutNode.ToString(), out var timeout))
                    {
                        if (timeout < 0 || timeout > 1440)
                            result.Errors.Add(new ValidationError("ssh.idle_timeout_minutes", "Idle timeout must be between 0 and 1440 minutes"));
                    }
                    else
                    {
                        result.Errors.Add(new ValidationError("ssh.idle_timeout_minutes", "Idle timeout must be an integer"));
                    }
                }

                // Warning for root login
                if (GetBool(sshMap, "root_login"))
                    result.Warnings.Add(new ValidationWarning("ssh.root_login", "Root SSH login enabled — consider using a non-root user"));
            }
        }

        return Task.FromResult(result);
    }

    public Task<ContainerTemplate> ParseYamlToTemplateAsync(string yaml, CancellationToken ct = default)
    {
        var yamlStream = new YamlStream();
        yamlStream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)yamlStream.Documents[0].RootNode;

        var template = new ContainerTemplate
        {
            Code = GetString(root, "code") ?? "",
            Name = GetString(root, "name") ?? GetString(root, "code") ?? "",
            Version = GetString(root, "version") ?? "1.0",
            BaseImage = GetString(root, "base_image") ?? "",
            Description = GetString(root, "description"),
        };

        if (root.Children.TryGetValue("tags", out var tagsNode) && tagsNode is YamlSequenceNode tagsSeq)
            template.Tags = tagsSeq.Children.Select(t => t.ToString()).ToArray();

        if (root.Children.TryGetValue("ide_type", out var ideNode))
        {
            if (Enum.TryParse<IdeType>(ideNode.ToString(), true, out var ideType))
                template.IdeType = ideType;
        }

        if (root.Children.TryGetValue("scope", out var scopeNode))
        {
            if (Enum.TryParse<CatalogScope>(scopeNode.ToString(), true, out var scope))
                template.CatalogScope = scope;
        }

        // Parse SSH section into JSON
        if (root.Children.TryGetValue("ssh", out var sshNode) && sshNode is YamlMappingNode sshMap)
        {
            var sshConfig = new SshConfig
            {
                Enabled = GetBool(sshMap, "enabled"),
                Port = GetInt(sshMap, "port", 22),
                RootLogin = GetBool(sshMap, "root_login"),
                IdleTimeoutMinutes = GetInt(sshMap, "idle_timeout_minutes", 60)
            };

            if (sshMap.Children.TryGetValue("auth_methods", out var authNode) && authNode is YamlSequenceNode authSeq)
                sshConfig.AuthMethods = authSeq.Children.Select(m => m.ToString()).ToList();

            template.SshConfiguration = JsonSerializer.Serialize(sshConfig);
        }

        return Task.FromResult(template);
    }

    private static string? GetString(YamlMappingNode map, string key)
    {
        return map.Children.TryGetValue(key, out var node) ? node.ToString() : null;
    }

    private static bool GetBool(YamlMappingNode map, string key)
    {
        return map.Children.TryGetValue(key, out var node)
            && bool.TryParse(node.ToString(), out var value) && value;
    }

    private static int GetInt(YamlMappingNode map, string key, int defaultValue)
    {
        return map.Children.TryGetValue(key, out var node)
            && int.TryParse(node.ToString(), out var value) ? value : defaultValue;
    }
}

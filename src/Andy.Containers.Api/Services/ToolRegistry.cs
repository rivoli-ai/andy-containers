using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Registry of known tools and their version detection commands.
/// </summary>
public static class ToolRegistry
{
    public record ToolDefinition(
        string Name,
        DependencyType Type,
        string DetectionCommand,
        bool UsesStdErr = false,
        string? WhichCommand = null);

    public static IReadOnlyList<ToolDefinition> KnownTools { get; } =
    [
        new("dotnet-sdk", DependencyType.Sdk, "dotnet --list-sdks", WhichCommand: "dotnet"),
        new("dotnet-runtime", DependencyType.Runtime, "dotnet --list-runtimes", WhichCommand: "dotnet"),
        new("python", DependencyType.Runtime, "python3 --version", WhichCommand: "python3"),
        new("node", DependencyType.Runtime, "node --version", WhichCommand: "node"),
        new("npm", DependencyType.Tool, "npm --version", WhichCommand: "npm"),
        new("go", DependencyType.Compiler, "go version", WhichCommand: "go"),
        new("rustc", DependencyType.Compiler, "rustc --version", WhichCommand: "rustc"),
        new("java", DependencyType.Runtime, "java --version", UsesStdErr: true, WhichCommand: "java"),
        new("git", DependencyType.Tool, "git --version", WhichCommand: "git"),
        new("docker", DependencyType.Tool, "docker --version", WhichCommand: "docker"),
        new("kubectl", DependencyType.Tool, "kubectl version --client -o json", WhichCommand: "kubectl"),
        new("code-server", DependencyType.Tool, "code-server --version", WhichCommand: "code-server"),
        new("ssh", DependencyType.Tool, "ssh -V", UsesStdErr: true, WhichCommand: "ssh"),
        new("curl", DependencyType.Tool, "curl --version", WhichCommand: "curl"),
        new("angular-cli", DependencyType.Tool, "ng version", WhichCommand: "ng"),
    ];
}

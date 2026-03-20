using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ToolVersionDetector : IToolVersionDetector
{
    private static readonly TimeSpan IntrospectionTimeout = TimeSpan.FromSeconds(60);

    private readonly IContainerService _containerService;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ILogger<ToolVersionDetector> _logger;

    public ToolVersionDetector(
        IContainerService containerService,
        IInfrastructureProviderFactory providerFactory,
        ILogger<ToolVersionDetector> logger)
    {
        _containerService = containerService;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public async Task<ImageToolManifest> IntrospectContainerAsync(
        Guid containerId,
        IReadOnlyList<DependencySpec>? declaredDependencies = null,
        CancellationToken ct = default)
    {
        var script = IntrospectionScriptBuilder.BuildScript();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IntrospectionTimeout);

        ExecResult result;
        try
        {
            result = await _containerService.ExecAsync(containerId, script, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Introspection timed out for container {ContainerId}", containerId);
            return CreateEmptyManifest();
        }

        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StdOut))
        {
            _logger.LogWarning("Introspection failed for container {ContainerId}: exit={ExitCode} stderr={StdErr}",
                containerId, result.ExitCode, result.StdErr);
            return CreateEmptyManifest();
        }

        return ParseIntrospectionOutput(result.StdOut ?? "", declaredDependencies);
    }

    public async Task<ImageToolManifest> IntrospectImageAsync(
        string imageReference,
        IReadOnlyList<DependencySpec>? declaredDependencies = null,
        CancellationToken ct = default)
    {
        // Create a temporary container from the image with network disabled
        var provider = _providerFactory.GetProvider(ProviderType.Docker);

        var spec = new ContainerSpec
        {
            ImageReference = imageReference,
            Name = $"introspect-{Guid.NewGuid():N}",
            Command = "sleep",
            Arguments = ["3600"],
            Labels = new Dictionary<string, string>
            {
                ["andy.purpose"] = "introspection",
                ["andy.ephemeral"] = "true"
            }
        };

        ContainerProvisionResult? provision = null;
        try
        {
            provision = await provider.CreateContainerAsync(spec, ct);
            await provider.StartContainerAsync(provision.ExternalId, ct);

            var script = IntrospectionScriptBuilder.BuildScript();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(IntrospectionTimeout);

            var result = await provider.ExecAsync(provision.ExternalId, script, IntrospectionTimeout, cts.Token);

            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StdOut))
            {
                _logger.LogWarning("Image introspection failed for {Image}: exit={ExitCode}", imageReference, result.ExitCode);
                return CreateEmptyManifest();
            }

            return ParseIntrospectionOutput(result.StdOut ?? "", declaredDependencies);
        }
        finally
        {
            if (provision is not null)
            {
                try
                {
                    await provider.DestroyContainerAsync(provision.ExternalId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup introspection container {ExternalId}", provision.ExternalId);
                }
            }
        }
    }

    public static ImageToolManifest ParseIntrospectionOutput(
        string output,
        IReadOnlyList<DependencySpec>? declaredDependencies)
    {
        var tools = new List<InstalledTool>();
        var packages = new List<InstalledPackage>();
        string architecture = "unknown";
        string kernel = "unknown";
        OsInfo? osInfo = null;

        // Build a lookup of declared dependencies by name for matching
        var declaredByName = declaredDependencies?
            .ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DependencySpec>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0) continue;

            var type = line[..tabIndex];
            var rest = line[(tabIndex + 1)..];

            switch (type)
            {
                case "ARCH":
                    architecture = rest.Trim();
                    break;

                case "KERNEL":
                    kernel = rest.Trim();
                    break;

                case "OS_RELEASE":
                    // Pipe-separated os-release lines — convert back to newlines
                    var osReleaseContent = rest.Replace('|', '\n');
                    osInfo = VersionParser.ParseOsRelease(osReleaseContent);
                    break;

                case "TOOL":
                    var tool = ParseToolLine(rest, declaredByName);
                    if (tool is not null)
                        tools.Add(tool);
                    break;

                case "PACKAGES":
                    // Pipe-separated dpkg/apk lines — convert back to newlines
                    var pkgContent = rest.Replace('|', '\n');
                    packages = VersionParser.ParseDpkgQuery(pkgContent);
                    break;
            }
        }

        osInfo ??= new OsInfo { Name = "Unknown", Version = "Unknown", Codename = "Unknown", KernelVersion = kernel };
        osInfo.KernelVersion = kernel;

        return new ImageToolManifest
        {
            ImageContentHash = "", // Will be computed by ContentHashCalculator
            BaseImage = "",        // Will be set by caller
            BaseImageDigest = "",  // Will be set by caller
            Architecture = NormalizeArchitecture(architecture),
            OperatingSystem = osInfo,
            Tools = tools,
            OsPackages = packages
        };
    }

    private static InstalledTool? ParseToolLine(string rest, Dictionary<string, DependencySpec> declaredByName)
    {
        // Format: {name}\t{version output}\t{binary path}
        var parts = rest.Split('\t');
        if (parts.Length < 2) return null;

        var name = parts[0].Trim();
        var rawOutput = parts[1].Trim();
        var binaryPath = parts.Length >= 3 ? parts[2].Trim() : null;

        // Find the tool definition to get its type and parser
        var toolDef = ToolRegistry.KnownTools.FirstOrDefault(t => t.Name == name);
        if (toolDef is null) return null;

        // Parse the version using the appropriate parser
        var version = ParseVersionForTool(name, rawOutput);
        if (version is null) return null;

        // Check against declared dependencies
        string? declaredVersion = null;
        bool matchesDeclared = true;

        if (declaredByName.TryGetValue(name, out var spec))
        {
            declaredVersion = spec.VersionConstraint;
            matchesDeclared = VersionConstraintMatcher.Matches(declaredVersion, version);
        }

        return new InstalledTool
        {
            Name = name,
            Version = version,
            Type = toolDef.Type,
            DeclaredVersion = declaredVersion,
            MatchesDeclared = matchesDeclared,
            BinaryPath = string.IsNullOrWhiteSpace(binaryPath) ? null : binaryPath
        };
    }

    private static string? ParseVersionForTool(string name, string output)
    {
        return name switch
        {
            "dotnet-sdk" => VersionParser.ParseDotnetSdk(output),
            "dotnet-runtime" => VersionParser.ParseDotnetRuntime(output),
            "python" => VersionParser.ParsePython(output),
            "node" => VersionParser.ParseNode(output),
            "npm" => VersionParser.ParseNpm(output),
            "go" => VersionParser.ParseGo(output),
            "rustc" => VersionParser.ParseRust(output),
            "java" => VersionParser.ParseJava(output),
            "git" => VersionParser.ParseGit(output),
            "docker" => VersionParser.ParseDocker(output),
            "kubectl" => VersionParser.ParseKubectl(output),
            "code-server" => VersionParser.ParseCodeServer(output),
            "ssh" => VersionParser.ParseOpenSsh(output),
            "curl" => VersionParser.ParseCurl(output),
            "angular-cli" => VersionParser.ParseAngularCli(output),
            _ => null
        };
    }

    private static string NormalizeArchitecture(string arch)
    {
        return arch switch
        {
            "x86_64" => "amd64",
            "aarch64" => "arm64",
            _ => arch
        };
    }

    private static ImageToolManifest CreateEmptyManifest() => new()
    {
        ImageContentHash = "",
        BaseImage = "",
        BaseImageDigest = "",
        Architecture = "unknown",
        OperatingSystem = new OsInfo
        {
            Name = "Unknown",
            Version = "Unknown",
            Codename = "Unknown",
            KernelVersion = "Unknown"
        }
    };
}

using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IToolVersionDetector
{
    string GenerateIntrospectionScript();
    IReadOnlyList<InstalledTool> ParseIntrospectionOutput(string jsonOutput, IReadOnlyList<DependencySpec>? declaredDeps = null);
    OsInfo ParseOsInfo(string osReleaseContent, string unameArch, string unameKernel);
    IReadOnlyList<InstalledPackage> ParseDpkgOutput(string dpkgOutput);
}

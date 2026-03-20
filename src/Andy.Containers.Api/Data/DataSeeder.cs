using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(ContainersDbContext db)
    {
        if (await db.Providers.AnyAsync())
            return;

        // Seed infrastructure providers
        db.Providers.AddRange(
            new InfrastructureProvider
            {
                Id = Guid.Parse("00000001-0001-0001-0001-000000000001"),
                Code = "local-docker",
                Name = "Local Docker",
                Type = ProviderType.Docker,
                Region = "local",
                IsEnabled = true,
                HealthStatus = ProviderHealth.Unknown,
                ConnectionConfig = """{"endpoint":"unix:///var/run/docker.sock"}""",
                Capabilities = """{"architectures":["arm64","amd64"],"operatingSystems":["linux"],"maxCpuCores":8,"maxMemoryMb":16384,"maxDiskGb":100,"supportsGpu":false,"supportsVolumeMount":true,"supportsPortForwarding":true,"supportsExec":true}"""
            },
            new InfrastructureProvider
            {
                Id = Guid.Parse("00000001-0001-0001-0001-000000000002"),
                Code = "apple-container-local",
                Name = "Local Apple Container",
                Type = ProviderType.AppleContainer,
                Region = "local",
                IsEnabled = true,
                HealthStatus = ProviderHealth.Unknown,
                ConnectionConfig = """{"cliPath":"/usr/local/bin/container"}""",
                Capabilities = """{"architectures":["arm64"],"operatingSystems":["linux"],"maxCpuCores":8,"maxMemoryMb":16384,"maxDiskGb":50,"supportsGpu":false,"supportsVolumeMount":true,"supportsPortForwarding":true,"supportsExec":true}"""
            }
        );

        // Seed global templates
        var fullStackId = Guid.Parse("00000002-0001-0001-0001-000000000001");
        var agentSandboxId = Guid.Parse("00000002-0001-0001-0001-000000000002");
        var dotnetId = Guid.Parse("00000002-0001-0001-0001-000000000003");
        var pythonId = Guid.Parse("00000002-0001-0001-0001-000000000004");
        var angularId = Guid.Parse("00000002-0001-0001-0001-000000000005");
        var andyCliId = Guid.Parse("00000002-0001-0001-0001-000000000006");

        db.Templates.AddRange(
            new ContainerTemplate
            {
                Id = fullStackId, Code = "full-stack", Name = "Full Stack Development",
                Description = "Complete environment with .NET 8, Python 3.12, Node 20, Angular 18",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet", "python", "node", "angular", "full-stack"],
                DefaultResources = """{"cpuCores":4,"memoryMb":8192,"diskGb":40}""",
                Scripts = """{"post_create":"if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq git curl wget ca-certificates >/dev/null 2>&1; elif command -v apk >/dev/null 2>&1; then apk add --quiet --no-cache git curl wget ca-certificates; elif command -v dnf >/dev/null 2>&1; then dnf install -y -q git curl wget ca-certificates; elif command -v yum >/dev/null 2>&1; then yum install -y -q git curl wget ca-certificates; elif command -v zypper >/dev/null 2>&1; then zypper install -y -n git curl wget ca-certificates; elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm git curl wget ca-certificates; fi"}"""
            },
            new ContainerTemplate
            {
                Id = agentSandboxId, Code = "agent-sandbox-ui", Name = "Agent Sandbox with UI",
                Description = "DevPilot AI agent environment with remote desktop and IDE",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.Both, GpuPreferred = true,
                IsPublished = true, Tags = ["agent", "devpilot", "ui", "vnc"],
                DefaultResources = """{"cpuCores":4,"memoryMb":8192,"diskGb":30}""",
                Scripts = """{"post_create":"if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq git curl wget ca-certificates >/dev/null 2>&1; elif command -v apk >/dev/null 2>&1; then apk add --quiet --no-cache git curl wget ca-certificates; elif command -v dnf >/dev/null 2>&1; then dnf install -y -q git curl wget ca-certificates; elif command -v yum >/dev/null 2>&1; then yum install -y -q git curl wget ca-certificates; elif command -v zypper >/dev/null 2>&1; then zypper install -y -n git curl wget ca-certificates; elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm git curl wget ca-certificates; fi"}"""
            },
            new ContainerTemplate
            {
                Id = dotnetId, Code = "dotnet-8-vscode", Name = ".NET 8 Development",
                Description = ".NET 8 SDK with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = """{"post_create":"if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq git curl wget ca-certificates >/dev/null 2>&1; elif command -v apk >/dev/null 2>&1; then apk add --quiet --no-cache git curl wget ca-certificates; elif command -v dnf >/dev/null 2>&1; then dnf install -y -q git curl wget ca-certificates; elif command -v yum >/dev/null 2>&1; then yum install -y -q git curl wget ca-certificates; elif command -v zypper >/dev/null 2>&1; then zypper install -y -n git curl wget ca-certificates; elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm git curl wget ca-certificates; fi"}"""
            },
            new ContainerTemplate
            {
                Id = pythonId, Code = "python-3.12-vscode", Name = "Python 3.12 Development",
                Description = "Python 3.12 with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["python"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = """{"post_create":"if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq git curl wget ca-certificates >/dev/null 2>&1; elif command -v apk >/dev/null 2>&1; then apk add --quiet --no-cache git curl wget ca-certificates; elif command -v dnf >/dev/null 2>&1; then dnf install -y -q git curl wget ca-certificates; elif command -v yum >/dev/null 2>&1; then yum install -y -q git curl wget ca-certificates; elif command -v zypper >/dev/null 2>&1; then zypper install -y -n git curl wget ca-certificates; elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm git curl wget ca-certificates; fi"}"""
            },
            new ContainerTemplate
            {
                Id = angularId, Code = "angular-18-vscode", Name = "Angular 18 Development",
                Description = "Node 20 + Angular 18 with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["angular", "node"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = """{"post_create":"if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq git curl wget ca-certificates >/dev/null 2>&1; elif command -v apk >/dev/null 2>&1; then apk add --quiet --no-cache git curl wget ca-certificates; elif command -v dnf >/dev/null 2>&1; then dnf install -y -q git curl wget ca-certificates; elif command -v yum >/dev/null 2>&1; then yum install -y -q git curl wget ca-certificates; elif command -v zypper >/dev/null 2>&1; then zypper install -y -n git curl wget ca-certificates; elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm git curl wget ca-certificates; fi"}"""
            },
            new ContainerTemplate
            {
                Id = andyCliId, Code = "andy-cli-dev", Name = "Andy CLI Development",
                Description = "Pre-installed Andy CLI environment",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["andy-cli", "dotnet", "ai"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = """{"post_create":"if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq git curl wget ca-certificates >/dev/null 2>&1; elif command -v apk >/dev/null 2>&1; then apk add --quiet --no-cache git curl wget ca-certificates; elif command -v dnf >/dev/null 2>&1; then dnf install -y -q git curl wget ca-certificates; elif command -v yum >/dev/null 2>&1; then yum install -y -q git curl wget ca-certificates; elif command -v zypper >/dev/null 2>&1; then zypper install -y -n git curl wget ca-certificates; elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm git curl wget ca-certificates; fi"}"""
            }
        );

        // Seed dependencies for full-stack template
        db.DependencySpecs.AddRange(
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "8.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Runtime, Name = "python", VersionConstraint = ">=3.12,<4.0", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 2 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "node", VersionConstraint = "20.x", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 3 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "angular-cli", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Major, SortOrder = 4 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 5 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 6 }
        );

        await db.SaveChangesAsync();
    }
}

using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Data;

public static class DataSeeder
{
    // Post-create script that works across popular Linux distros:
    // 1. Installs essential tools (git, curl, wget, ca-certificates)
    // 2. Installs and starts SSH server for remote access
    private const string PostCreateScript =
        // Install base packages
        "if command -v apt-get >/dev/null 2>&1; then " +
            "export DEBIAN_FRONTEND=noninteractive && " +
            "apt-get update -qq && " +
            "apt-get install -y -qq git curl wget ca-certificates openssh-server >/dev/null 2>&1; " +
        "elif command -v apk >/dev/null 2>&1; then " +
            "apk add --quiet --no-cache git curl wget ca-certificates openssh; " +
        "elif command -v dnf >/dev/null 2>&1; then " +
            "dnf install -y -q git curl wget ca-certificates openssh-server; " +
        "elif command -v yum >/dev/null 2>&1; then " +
            "yum install -y -q git curl wget ca-certificates openssh-server; " +
        "elif command -v zypper >/dev/null 2>&1; then " +
            "zypper install -y -n git curl wget ca-certificates openssh; " +
        "elif command -v pacman >/dev/null 2>&1; then " +
            "pacman -Sy --noconfirm git curl wget ca-certificates openssh; " +
        "fi && " +
        // Configure and start SSH
        "mkdir -p /run/sshd && " +
        "sed -i 's/#\\?PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config && " +
        "sed -i 's/#\\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config && " +
        "echo 'root:container' | chpasswd && " +
        "ssh-keygen -A 2>/dev/null; " +
        "/usr/sbin/sshd 2>/dev/null || true";

    private static string ScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript });

    public static async Task SeedAsync(ContainersDbContext db)
    {
        if (await db.Providers.AnyAsync())
        {
            // DB already seeded — update template scripts if they changed
            await UpdateTemplateScriptsAsync(db);
            // Backfill missing dependency specs for seed templates
            await BackfillDependencySpecsAsync(db);
            return;
        }

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
                Scripts = ScriptsJson
            },
            new ContainerTemplate
            {
                Id = agentSandboxId, Code = "agent-sandbox-ui", Name = "Agent Sandbox with UI",
                Description = "DevPilot AI agent environment with remote desktop and IDE",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.Both, GpuPreferred = true,
                IsPublished = true, Tags = ["agent", "devpilot", "ui", "vnc"],
                DefaultResources = """{"cpuCores":4,"memoryMb":8192,"diskGb":30}""",
                Scripts = ScriptsJson
            },
            new ContainerTemplate
            {
                Id = dotnetId, Code = "dotnet-8-vscode", Name = ".NET 8 Development",
                Description = ".NET 8 SDK with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = ScriptsJson
            },
            new ContainerTemplate
            {
                Id = pythonId, Code = "python-3.12-vscode", Name = "Python 3.12 Development",
                Description = "Python 3.12 with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["python"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = ScriptsJson
            },
            new ContainerTemplate
            {
                Id = angularId, Code = "angular-18-vscode", Name = "Angular 18 Development",
                Description = "Node 20 + Angular 18 with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["angular", "node"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = ScriptsJson
            },
            new ContainerTemplate
            {
                Id = andyCliId, Code = "andy-cli-dev", Name = "Andy CLI Development",
                Description = "Pre-installed Andy CLI environment",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["andy-cli", "dotnet", "ai"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = ScriptsJson
            }
        );

        // Seed dependencies for all templates
        SeedDependencySpecs(db, fullStackId, agentSandboxId, dotnetId, pythonId, angularId, andyCliId);

        await db.SaveChangesAsync();
    }

    private static void SeedDependencySpecs(ContainersDbContext db,
        Guid fullStackId, Guid agentSandboxId, Guid dotnetId, Guid pythonId, Guid angularId, Guid andyCliId)
    {
        db.DependencySpecs.AddRange(
            // Full Stack: dotnet + python + node + angular + git + code-server
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "8.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Runtime, Name = "python", VersionConstraint = ">=3.12,<4.0", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 2 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "node", VersionConstraint = "20.x", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 3 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "angular-cli", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Major, SortOrder = 4 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 5 },
            new DependencySpec { TemplateId = fullStackId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 6 },

            // Agent Sandbox: dotnet + python + node + code-server + git + xfce4 + tigervnc
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "8.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.Runtime, Name = "python", VersionConstraint = ">=3.12,<4.0", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 2 },
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.Tool, Name = "node", VersionConstraint = "20.x", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 3 },
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 4 },
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 5 },
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.OsPackage, Name = "xfce4", VersionConstraint = "latest", AutoUpdate = false, UpdatePolicy = UpdatePolicy.SecurityOnly, SortOrder = 6 },
            new DependencySpec { TemplateId = agentSandboxId, Type = DependencyType.OsPackage, Name = "tigervnc-standalone-server", VersionConstraint = "latest", AutoUpdate = false, UpdatePolicy = UpdatePolicy.SecurityOnly, SortOrder = 7 },

            // .NET 8: dotnet-sdk + git + code-server
            new DependencySpec { TemplateId = dotnetId, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "8.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = dotnetId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 2 },
            new DependencySpec { TemplateId = dotnetId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 3 },

            // Python 3.12: python + pip + git + code-server
            new DependencySpec { TemplateId = pythonId, Type = DependencyType.Runtime, Name = "python", VersionConstraint = ">=3.12,<4.0", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 1 },
            new DependencySpec { TemplateId = pythonId, Type = DependencyType.Tool, Name = "pip", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 2 },
            new DependencySpec { TemplateId = pythonId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 3 },
            new DependencySpec { TemplateId = pythonId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 4 },

            // Angular 18: node + angular-cli + git + code-server
            new DependencySpec { TemplateId = angularId, Type = DependencyType.Tool, Name = "node", VersionConstraint = "20.x", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 1 },
            new DependencySpec { TemplateId = angularId, Type = DependencyType.Tool, Name = "angular-cli", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Major, SortOrder = 2 },
            new DependencySpec { TemplateId = angularId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 3 },
            new DependencySpec { TemplateId = angularId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 4 },

            // Andy CLI: dotnet-sdk + andy-cli + git + code-server
            new DependencySpec { TemplateId = andyCliId, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "8.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = andyCliId, Type = DependencyType.Tool, Name = "andy-cli", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 2 },
            new DependencySpec { TemplateId = andyCliId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 3 },
            new DependencySpec { TemplateId = andyCliId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 4 }
        );
    }

    /// <summary>
    /// Updates existing seed templates' scripts when they change between versions.
    /// Only updates templates that were originally seeded (by well-known ID).
    /// </summary>
    private static async Task UpdateTemplateScriptsAsync(ContainersDbContext db)
    {
        var seedTemplateIds = new[]
        {
            Guid.Parse("00000002-0001-0001-0001-000000000001"),
            Guid.Parse("00000002-0001-0001-0001-000000000002"),
            Guid.Parse("00000002-0001-0001-0001-000000000003"),
            Guid.Parse("00000002-0001-0001-0001-000000000004"),
            Guid.Parse("00000002-0001-0001-0001-000000000005"),
            Guid.Parse("00000002-0001-0001-0001-000000000006"),
        };

        var templates = await db.Templates
            .Where(t => seedTemplateIds.Contains(t.Id))
            .ToListAsync();

        var updated = false;
        foreach (var template in templates)
        {
            // Compare by checking if the script contains expected content,
            // since JSON formatting may differ between C# and PostgreSQL
            if (template.Scripts is null || !template.Scripts.Contains("openssh"))
            {
                template.Scripts = ScriptsJson;
                updated = true;
            }
        }

        if (updated)
            await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds dependency specs for seed templates that are missing them.
    /// This handles upgrades where templates were seeded before dependency specs were added.
    /// </summary>
    private static async Task BackfillDependencySpecsAsync(ContainersDbContext db)
    {
        var fullStackId = Guid.Parse("00000002-0001-0001-0001-000000000001");
        var agentSandboxId = Guid.Parse("00000002-0001-0001-0001-000000000002");
        var dotnetId = Guid.Parse("00000002-0001-0001-0001-000000000003");
        var pythonId = Guid.Parse("00000002-0001-0001-0001-000000000004");
        var angularId = Guid.Parse("00000002-0001-0001-0001-000000000005");
        var andyCliId = Guid.Parse("00000002-0001-0001-0001-000000000006");

        var seedIds = new[] { fullStackId, agentSandboxId, dotnetId, pythonId, angularId, andyCliId };

        // Find which seed templates have no dependency specs at all
        var templatesWithDeps = await db.DependencySpecs
            .Where(d => seedIds.Contains(d.TemplateId))
            .Select(d => d.TemplateId)
            .Distinct()
            .ToListAsync();

        var missing = seedIds.Except(templatesWithDeps).ToList();
        if (missing.Count == 0)
            return;

        // Only seed deps for templates that have none — use a temporary context
        // to avoid duplicating the full-stack deps that may already exist
        SeedDependencySpecs(db, fullStackId, agentSandboxId, dotnetId, pythonId, angularId, andyCliId);

        // Remove specs for templates that already had them (we just re-added everything)
        var duplicates = db.ChangeTracker.Entries<DependencySpec>()
            .Where(e => e.State == EntityState.Added && !missing.Contains(e.Entity.TemplateId))
            .ToList();
        foreach (var dup in duplicates)
            dup.State = EntityState.Detached;

        await db.SaveChangesAsync();
    }
}

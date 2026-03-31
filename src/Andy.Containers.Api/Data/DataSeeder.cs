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
        // Ensure localhost resolves (minimal container images often have empty /etc/hosts)
        "grep -q localhost /etc/hosts 2>/dev/null || " +
            "{ echo '127.0.0.1 localhost' >> /etc/hosts && echo '::1 localhost' >> /etc/hosts; }; " +
        // Set UTF-8 locale for proper character rendering (tmux box-drawing, etc.)
        "echo 'LANG=C.UTF-8' >> /etc/environment; " +
        "echo 'LC_ALL=C.UTF-8' >> /etc/environment; " +
        "export LANG=C.UTF-8 LC_ALL=C.UTF-8; " +
        // Set SSL env vars for corporate environments (NuGet, dotnet CLI, curl)
        "echo 'DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0' >> /etc/environment; " +
        "echo 'NUGET_CERT_REVOCATION_MODE=off' >> /etc/environment; " +
        "echo 'DOTNET_NUGET_SIGNATURE_VERIFICATION=false' >> /etc/environment; " +
        // Install base packages
        "if command -v apt-get >/dev/null 2>&1; then " +
            "export DEBIAN_FRONTEND=noninteractive && " +
            "apt-get update -qq && " +
            "apt-get install -y -qq git curl wget ca-certificates openssh-server dtach tmux locales >/dev/null 2>&1 && " +
            "locale-gen en_US.UTF-8 >/dev/null 2>&1; " +
        "elif command -v apk >/dev/null 2>&1; then " +
            "apk add --quiet --no-cache git curl wget ca-certificates openssh dtach tmux; " +
        "elif command -v dnf >/dev/null 2>&1; then " +
            "dnf install -y -q git curl wget ca-certificates openssh-server dtach tmux; " +
        "elif command -v yum >/dev/null 2>&1; then " +
            "yum install -y -q git curl wget ca-certificates openssh-server dtach tmux; " +
        "elif command -v zypper >/dev/null 2>&1; then " +
            "zypper install -y -n git curl wget ca-certificates openssh dtach tmux; " +
        "elif command -v pacman >/dev/null 2>&1; then " +
            "pacman -Sy --noconfirm git curl wget ca-certificates openssh dtach tmux; " +
        "fi; " +
        // Install GitHub CLI (gh) — universal tarball approach works on all distros
        "GHARCH=$(uname -m | sed 's/x86_64/amd64/' | sed 's/aarch64/arm64/') && " +
        "curl -fsSL https://github.com/cli/cli/releases/latest/download/gh_$(curl -fsSL https://api.github.com/repos/cli/cli/releases/latest | grep -o '\"tag_name\":\"v[^\"]*' | cut -d'v' -f2)_linux_${GHARCH}.tar.gz 2>/dev/null | " +
        "tar xzf - --strip-components=1 -C /usr/local 2>/dev/null || true; " +
        // Configure and start SSH (use ; not && so failures don't stop the chain)
        "mkdir -p /run/sshd; " +
        "sed -i 's/#\\?PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config 2>/dev/null; " +
        "sed -i 's/#\\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config 2>/dev/null; " +
        "echo 'root:container' | chpasswd 2>/dev/null; " +
        "ssh-keygen -A 2>/dev/null; " +
        "/usr/sbin/sshd 2>/dev/null || true; " +
        // Ensure login shells source .bashrc (for color support in terminal)
        "grep -q bashrc /root/.bash_profile 2>/dev/null || " +
            "echo '[ -f ~/.bashrc ] && . ~/.bashrc' >> /root/.bash_profile";

    private static string ScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript });

    // Template-specific post-create scripts that install toolchains after base packages
    private static string PythonScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript + " && " +
            "apt-get install -y -qq python3 python3-pip python3-venv >/dev/null 2>&1 && " +
            "ln -sf /usr/bin/python3 /usr/bin/python 2>/dev/null; " +
            "ln -sf /usr/bin/pip3 /usr/bin/pip 2>/dev/null" });

    private static string DotnetScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript + " && " +
            // Use official install script (works without Microsoft repo registration)
            "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 && " +
            "ln -sf /root/.dotnet/dotnet /usr/local/bin/dotnet 2>/dev/null && " +
            "echo 'export DOTNET_ROOT=/root/.dotnet' >> /root/.bashrc && " +
            "echo 'export PATH=$PATH:/root/.dotnet:/root/.dotnet/tools' >> /root/.bashrc" });

    private static string Dotnet10ScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript + " && " +
            "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 && " +
            "ln -sf /root/.dotnet/dotnet /usr/local/bin/dotnet 2>/dev/null && " +
            "echo 'export DOTNET_ROOT=/root/.dotnet' >> /root/.bashrc && " +
            "echo 'export PATH=$PATH:/root/.dotnet:/root/.dotnet/tools' >> /root/.bashrc" });

    private static string NodeScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript + " && " +
            "curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && " +
            "apt-get install -y -qq nodejs >/dev/null 2>&1" });

    // Alpine-based .NET 8: base image already has .NET SDK, just add bash and build tools
    private static string DotnetAlpineScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript + " && " +
            "apk add --quiet --no-cache bash build-base icu-libs >/dev/null 2>&1 && " +
            "echo 'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false' >> /root/.bashrc" });

    private static string FullStackScriptsJson { get; } = JsonSerializer.Serialize(
        new Dictionary<string, string> { ["post_create"] = PostCreateScript + " && " +
            // Python
            "apt-get install -y -qq python3 python3-pip python3-venv >/dev/null 2>&1 && " +
            "ln -sf /usr/bin/python3 /usr/bin/python 2>/dev/null; " +
            "ln -sf /usr/bin/pip3 /usr/bin/pip 2>/dev/null && " +
            // Node
            "curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && " +
            "apt-get install -y -qq nodejs >/dev/null 2>&1 && " +
            // .NET
            "{ curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 && " +
            "ln -sf /root/.dotnet/dotnet /usr/local/bin/dotnet 2>/dev/null && " +
            "echo 'export DOTNET_ROOT=/root/.dotnet' >> /root/.bashrc && " +
            "echo 'export PATH=$PATH:/root/.dotnet:/root/.dotnet/tools' >> /root/.bashrc; } || true" });

    public static async Task SeedAsync(ContainersDbContext db)
    {
        if (await db.Providers.AnyAsync())
        {
            // DB already seeded — add new seed templates, update scripts, backfill deps
            await AddNewSeedTemplatesAsync(db);
            await UpdateTemplateScriptsAsync(db);
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
        var dotnet10Id = Guid.Parse("00000002-0001-0001-0001-000000000007");
        var dotnetAlpineId = Guid.Parse("00000002-0001-0001-0001-000000000008");

        db.Templates.AddRange(
            new ContainerTemplate
            {
                Id = fullStackId, Code = "full-stack", Name = "Full Stack Development",
                Description = "Complete environment with .NET 8, Python 3.12, Node 20, Angular 18",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet", "python", "node", "angular", "full-stack"],
                DefaultResources = """{"cpuCores":4,"memoryMb":8192,"diskGb":40}""",
                Scripts = FullStackScriptsJson
            },
            new ContainerTemplate
            {
                Id = agentSandboxId, Code = "agent-sandbox-ui", Name = "Agent Sandbox with UI",
                Description = "DevPilot AI agent environment with remote desktop and IDE",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.Both, GuiType = "vnc", GpuPreferred = true,
                IsPublished = true, Tags = ["agent", "devpilot", "ui", "vnc"],
                DefaultResources = """{"cpuCores":4,"memoryMb":8192,"diskGb":30}""",
                Scripts = FullStackScriptsJson,
                CodeAssistant = """{"Tool":"ClaudeCode","AutoStart":false,"ApiKeyEnvVar":"ANTHROPIC_API_KEY"}"""
            },
            new ContainerTemplate
            {
                Id = dotnetId, Code = "dotnet-8-vscode", Name = ".NET 8 Development",
                Description = ".NET 8 SDK with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = DotnetScriptsJson
            },
            new ContainerTemplate
            {
                Id = pythonId, Code = "python-3.12-vscode", Name = "Python 3.12 Development",
                Description = "Python 3.12 with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["python"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = PythonScriptsJson
            },
            new ContainerTemplate
            {
                Id = angularId, Code = "angular-18-vscode", Name = "Angular 18 Development",
                Description = "Node 20 + Angular 18 with VSCode",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["angular", "node"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = NodeScriptsJson
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
            },
            new ContainerTemplate
            {
                Id = dotnet10Id, Code = "dotnet-10-cli", Name = ".NET 10 CLI Development",
                Description = ".NET 10 SDK for CLI and API development",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet", "dotnet-10"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = Dotnet10ScriptsJson
            },
            new ContainerTemplate
            {
                Id = dotnetAlpineId, Code = "dotnet-8-alpine", Name = ".NET 8 Alpine (Minimal)",
                Description = "Minimal .NET 8 development environment based on Alpine Linux with essential dev tools",
                Version = "1.0.0", BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0-alpine",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet", "alpine", "minimal"],
                DefaultResources = """{"cpuCores":2,"memoryMb":2048,"diskGb":10}""",
                Scripts = DotnetAlpineScriptsJson
            }
        );

        // Seed dependencies for all templates
        SeedDependencySpecs(db, fullStackId, agentSandboxId, dotnetId, pythonId, angularId, andyCliId, dotnet10Id, dotnetAlpineId);

        await db.SaveChangesAsync();
    }

    private static void SeedDependencySpecs(ContainersDbContext db,
        Guid fullStackId, Guid agentSandboxId, Guid dotnetId, Guid pythonId, Guid angularId, Guid andyCliId, Guid dotnet10Id, Guid dotnetAlpineId)
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
            new DependencySpec { TemplateId = andyCliId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 4 },

            // .NET 10 CLI: dotnet-sdk 10 + git + code-server
            new DependencySpec { TemplateId = dotnet10Id, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "10.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = dotnet10Id, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 2 },
            new DependencySpec { TemplateId = dotnet10Id, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 3 },

            // .NET 8 Alpine: dotnet-sdk (pre-installed in base image) + git + code-server + build-base + bash
            new DependencySpec { TemplateId = dotnetAlpineId, Type = DependencyType.Sdk, Name = "dotnet-sdk", VersionConstraint = "8.0.*", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 1 },
            new DependencySpec { TemplateId = dotnetAlpineId, Type = DependencyType.Tool, Name = "git", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Patch, SortOrder = 2 },
            new DependencySpec { TemplateId = dotnetAlpineId, Type = DependencyType.Tool, Name = "code-server", VersionConstraint = "latest", AutoUpdate = true, UpdatePolicy = UpdatePolicy.Minor, SortOrder = 3 },
            new DependencySpec { TemplateId = dotnetAlpineId, Type = DependencyType.OsPackage, Name = "build-base", VersionConstraint = "latest", AutoUpdate = false, UpdatePolicy = UpdatePolicy.SecurityOnly, SortOrder = 4 },
            new DependencySpec { TemplateId = dotnetAlpineId, Type = DependencyType.OsPackage, Name = "bash", VersionConstraint = "latest", AutoUpdate = false, UpdatePolicy = UpdatePolicy.SecurityOnly, SortOrder = 5 }
        );
    }

    /// <summary>
    /// Adds new seed templates that were introduced in a later version.
    /// Only adds templates with well-known IDs that don't exist yet.
    /// </summary>
    private static async Task AddNewSeedTemplatesAsync(ContainersDbContext db)
    {
        var dotnet10Id = Guid.Parse("00000002-0001-0001-0001-000000000007");
        var dotnetAlpineId = Guid.Parse("00000002-0001-0001-0001-000000000008");

        // Check which new templates are missing
        var newTemplates = new Dictionary<Guid, ContainerTemplate>
        {
            [dotnet10Id] = new ContainerTemplate
            {
                Id = dotnet10Id, Code = "dotnet-10-cli", Name = ".NET 10 CLI Development",
                Description = ".NET 10 SDK for CLI and API development",
                Version = "1.0.0", BaseImage = "ubuntu:24.04",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet", "dotnet-10"],
                DefaultResources = """{"cpuCores":2,"memoryMb":4096,"diskGb":20}""",
                Scripts = Dotnet10ScriptsJson
            },
            [dotnetAlpineId] = new ContainerTemplate
            {
                Id = dotnetAlpineId, Code = "dotnet-8-alpine", Name = ".NET 8 Alpine (Minimal)",
                Description = "Minimal .NET 8 development environment based on Alpine Linux with essential dev tools",
                Version = "1.0.0", BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0-alpine",
                CatalogScope = CatalogScope.Global, IdeType = IdeType.CodeServer,
                IsPublished = true, Tags = ["dotnet", "alpine", "minimal"],
                DefaultResources = """{"cpuCores":2,"memoryMb":2048,"diskGb":10}""",
                Scripts = DotnetAlpineScriptsJson
            }
        };

        var existingIds = await db.Templates
            .Where(t => newTemplates.Keys.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();

        var toAdd = newTemplates.Where(kv => !existingIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (toAdd.Count > 0)
        {
            db.Templates.AddRange(toAdd);
            await db.SaveChangesAsync();
        }
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
            Guid.Parse("00000002-0001-0001-0001-000000000007"),
            Guid.Parse("00000002-0001-0001-0001-000000000008"),
        };

        var templates = await db.Templates
            .Where(t => seedTemplateIds.Contains(t.Id))
            .ToListAsync();

        var scriptsByCode = new Dictionary<string, string>
        {
            ["full-stack"] = FullStackScriptsJson,
            ["agent-sandbox-ui"] = FullStackScriptsJson,
            ["dotnet-8-vscode"] = DotnetScriptsJson,
            ["python-3.12-vscode"] = PythonScriptsJson,
            ["angular-18-vscode"] = NodeScriptsJson,
            ["andy-cli-dev"] = ScriptsJson,
            ["dotnet-10-cli"] = Dotnet10ScriptsJson,
            ["dotnet-8-alpine"] = DotnetAlpineScriptsJson,
        };

        var updated = false;
        foreach (var template in templates)
        {
            var expected = scriptsByCode.GetValueOrDefault(template.Code, ScriptsJson);
            if (template.Scripts != expected)
            {
                template.Scripts = expected;
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
        var dotnet10Id = Guid.Parse("00000002-0001-0001-0001-000000000007");
        var dotnetAlpineId = Guid.Parse("00000002-0001-0001-0001-000000000008");

        var seedIds = new[] { fullStackId, agentSandboxId, dotnetId, pythonId, angularId, andyCliId, dotnet10Id, dotnetAlpineId };

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
        SeedDependencySpecs(db, fullStackId, agentSandboxId, dotnetId, pythonId, angularId, andyCliId, dotnet10Id, dotnetAlpineId);

        // Remove specs for templates that already had them (we just re-added everything)
        var duplicates = db.ChangeTracker.Entries<DependencySpec>()
            .Where(e => e.State == EntityState.Added && !missing.Contains(e.Entity.TemplateId))
            .ToList();
        foreach (var dup in duplicates)
            dup.State = EntityState.Detached;

        await db.SaveChangesAsync();
    }
}

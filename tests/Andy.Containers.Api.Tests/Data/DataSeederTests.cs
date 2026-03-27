using Andy.Containers.Api.Data;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Api.Tests.Data;

public class DataSeederTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    [Fact]
    public async Task SeedAsync_CreatesAllTemplates()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        templates.Should().HaveCount(7);
        templates.Select(t => t.Code).Should().BeEquivalentTo(
            "full-stack", "agent-sandbox-ui", "dotnet-8-vscode",
            "python-3.12-vscode", "angular-18-vscode", "andy-cli-dev", "dotnet-10-cli");
    }

    [Fact]
    public async Task SeedAsync_CreatesProviders()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var providers = await db.Providers.ToListAsync();
        providers.Should().HaveCount(2);
        providers.Select(p => p.Code).Should().BeEquivalentTo("local-docker", "apple-container-local");
    }

    [Fact]
    public async Task SeedAsync_SeedsDependencySpecsForAllTemplates()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        foreach (var template in templates)
        {
            var deps = await db.DependencySpecs
                .Where(d => d.TemplateId == template.Id)
                .ToListAsync();
            deps.Should().NotBeEmpty($"template '{template.Code}' should have dependency specs");
        }
    }

    [Theory]
    [InlineData("full-stack", new[] { "dotnet-sdk", "python", "node", "angular-cli", "git", "code-server" })]
    [InlineData("agent-sandbox-ui", new[] { "dotnet-sdk", "python", "node", "code-server", "git", "xfce4", "tigervnc-standalone-server" })]
    [InlineData("dotnet-8-vscode", new[] { "dotnet-sdk", "git", "code-server" })]
    [InlineData("python-3.12-vscode", new[] { "python", "pip", "git", "code-server" })]
    [InlineData("angular-18-vscode", new[] { "node", "angular-cli", "git", "code-server" })]
    [InlineData("andy-cli-dev", new[] { "dotnet-sdk", "andy-cli", "git", "code-server" })]
    [InlineData("dotnet-10-cli", new[] { "dotnet-sdk", "git", "code-server" })]
    public async Task SeedAsync_TemplateHasExpectedDependencies(string templateCode, string[] expectedDeps)
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.FirstAsync(t => t.Code == templateCode);
        var deps = await db.DependencySpecs
            .Where(d => d.TemplateId == template.Id)
            .OrderBy(d => d.SortOrder)
            .ToListAsync();

        deps.Select(d => d.Name).Should().BeEquivalentTo(expectedDeps);
    }

    [Fact]
    public async Task SeedAsync_DependencyTypesAreCorrect()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var deps = await db.DependencySpecs.ToListAsync();

        // SDKs
        deps.Where(d => d.Name == "dotnet-sdk").Should().AllSatisfy(d =>
            d.Type.Should().Be(DependencyType.Sdk));

        // Runtimes
        deps.Where(d => d.Name == "python").Should().AllSatisfy(d =>
            d.Type.Should().Be(DependencyType.Runtime));

        // Tools
        deps.Where(d => d.Name is "git" or "code-server" or "node" or "angular-cli" or "pip" or "andy-cli")
            .Should().AllSatisfy(d => d.Type.Should().Be(DependencyType.Tool));

        // OS packages
        deps.Where(d => d.Name is "xfce4" or "tigervnc-standalone-server")
            .Should().AllSatisfy(d => d.Type.Should().Be(DependencyType.OsPackage));
    }

    [Fact]
    public async Task SeedAsync_UpdatePoliciesAreSensible()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var deps = await db.DependencySpecs.ToListAsync();

        // SDKs should use Patch policy (conservative)
        deps.Where(d => d.Type == DependencyType.Sdk).Should().AllSatisfy(d =>
            d.UpdatePolicy.Should().Be(UpdatePolicy.Patch));

        // OS packages for VNC/desktop should use SecurityOnly
        deps.Where(d => d.Type == DependencyType.OsPackage).Should().AllSatisfy(d =>
            d.UpdatePolicy.Should().Be(UpdatePolicy.SecurityOnly));

        // git should use Patch policy
        deps.Where(d => d.Name == "git").Should().AllSatisfy(d =>
            d.UpdatePolicy.Should().Be(UpdatePolicy.Patch));
    }

    [Fact]
    public async Task SeedAsync_AllTemplatesIncludeGit()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        foreach (var template in templates)
        {
            var hasGit = await db.DependencySpecs
                .AnyAsync(d => d.TemplateId == template.Id && d.Name == "git");
            hasGit.Should().BeTrue($"template '{template.Code}' should include git");
        }
    }

    [Fact]
    public async Task SeedAsync_AllTemplateScriptsIncludeLocalhostFix()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        foreach (var template in templates)
        {
            template.Scripts.Should().NotBeNullOrEmpty($"template '{template.Code}' should have scripts");
            template.Scripts.Should().Contain("localhost",
                $"template '{template.Code}' scripts should ensure /etc/hosts has localhost");
            template.Scripts.Should().Contain("127.0.0.1",
                $"template '{template.Code}' scripts should add 127.0.0.1 localhost mapping");
        }
    }

    [Fact]
    public async Task SeedAsync_AllTemplateScriptsIncludeUtf8Locale()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        foreach (var template in templates)
        {
            template.Scripts.Should().Contain("LANG=C.UTF-8",
                $"template '{template.Code}' scripts should set UTF-8 locale");
            template.Scripts.Should().Contain("LC_ALL=C.UTF-8",
                $"template '{template.Code}' scripts should set LC_ALL to UTF-8");
        }
    }

    [Fact]
    public async Task SeedAsync_AllTemplateScriptsInstallTmux()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        foreach (var template in templates)
        {
            template.Scripts.Should().Contain("tmux",
                $"template '{template.Code}' scripts should install tmux");
        }
    }

    [Theory]
    [InlineData("python-3.12-vscode", "python3")]
    [InlineData("dotnet-8-vscode", "dotnet")]
    [InlineData("dotnet-10-cli", "dotnet")]
    [InlineData("angular-18-vscode", "nodejs")]
    [InlineData("full-stack", "python3")]
    [InlineData("full-stack", "nodejs")]
    [InlineData("full-stack", "dotnet")]
    public async Task SeedAsync_TemplateScriptsInstallCorrectToolchain(string templateCode, string expectedToolchain)
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.FirstAsync(t => t.Code == templateCode);
        template.Scripts.Should().Contain(expectedToolchain,
            $"template '{templateCode}' should install {expectedToolchain}");
    }

    [Theory]
    [InlineData("andy-cli-dev", "python3")]
    [InlineData("andy-cli-dev", "nodejs")]
    [InlineData("dotnet-8-vscode", "python3")]
    [InlineData("angular-18-vscode", "dotnet")]
    [InlineData("python-3.12-vscode", "dotnet")]
    [InlineData("dotnet-10-cli", "python3")]
    [InlineData("dotnet-10-cli", "nodejs")]
    public async Task SeedAsync_TemplateScriptsDoNotInstallUnrelatedToolchain(string templateCode, string unexpectedToolchain)
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.FirstAsync(t => t.Code == templateCode);
        template.Scripts.Should().NotContain(unexpectedToolchain,
            $"template '{templateCode}' should NOT install {unexpectedToolchain}");
    }

    [Fact]
    public async Task SeedAsync_AllTemplateScriptsInstallLocalesPackage()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var templates = await db.Templates.ToListAsync();
        foreach (var template in templates)
        {
            template.Scripts.Should().Contain("locales",
                $"template '{template.Code}' scripts should install locales package");
            template.Scripts.Should().Contain("locale-gen",
                $"template '{template.Code}' scripts should run locale-gen");
        }
    }

    [Fact]
    public async Task SeedAsync_DotnetTemplatesUseDotnetInstallScript()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var dotnet8 = await db.Templates.FirstAsync(t => t.Code == "dotnet-8-vscode");
        dotnet8.Scripts.Should().Contain("dotnet-install.sh",
            ".NET 8 template should use official install script");
        dotnet8.Scripts.Should().Contain("--channel 8.0");
        dotnet8.Scripts.Should().Contain("DOTNET_ROOT",
            ".NET 8 template should set DOTNET_ROOT in bashrc");

        var dotnet10 = await db.Templates.FirstAsync(t => t.Code == "dotnet-10-cli");
        dotnet10.Scripts.Should().Contain("dotnet-install.sh",
            ".NET 10 template should use official install script");
        dotnet10.Scripts.Should().Contain("--channel 10.0");
        dotnet10.Scripts.Should().Contain("DOTNET_ROOT",
            ".NET 10 template should set DOTNET_ROOT in bashrc");
    }

    [Fact]
    public async Task SeedAsync_UpdateTemplateScripts_WhenReseeded()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        // Tamper with a template's scripts
        var python = await db.Templates.FirstAsync(t => t.Code == "python-3.12-vscode");
        python.Scripts = "{}";
        await db.SaveChangesAsync();

        // Re-seed should fix it
        await DataSeeder.SeedAsync(db);

        var updated = await db.Templates.FirstAsync(t => t.Code == "python-3.12-vscode");
        updated.Scripts.Should().Contain("python3",
            "re-seeding should restore template-specific scripts");
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using var db = InMemoryDbHelper.CreateContext(_dbName);
        await DataSeeder.SeedAsync(db);

        var countBefore = await db.DependencySpecs.CountAsync();

        // Run again — should not duplicate
        await DataSeeder.SeedAsync(db);

        var countAfter = await db.DependencySpecs.CountAsync();
        countAfter.Should().Be(countBefore);
    }

    [Fact]
    public async Task SeedAsync_BackfillsDependencySpecs_WhenTemplatesExistWithoutDeps()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = InMemoryDbHelper.CreateContext(dbName))
        {
            // First seed creates everything
            await DataSeeder.SeedAsync(db);

            // Remove all dependency specs to simulate pre-upgrade state
            db.DependencySpecs.RemoveRange(db.DependencySpecs);
            await db.SaveChangesAsync();
            (await db.DependencySpecs.CountAsync()).Should().Be(0);
        }

        using (var db = InMemoryDbHelper.CreateContext(dbName))
        {
            // Second seed should backfill
            await DataSeeder.SeedAsync(db);

            var deps = await db.DependencySpecs.ToListAsync();
            deps.Should().NotBeEmpty("backfill should add dependency specs for seed templates");

            // Every seed template should have deps
            var templateIds = await db.Templates.Select(t => t.Id).ToListAsync();
            foreach (var tid in templateIds)
            {
                deps.Where(d => d.TemplateId == tid).Should().NotBeEmpty();
            }
        }
    }
}

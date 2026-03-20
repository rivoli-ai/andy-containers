using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class TemplateYamlPersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TemplateYamlPersistence _persistence;

    public TemplateYamlPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yaml-persist-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _persistence = new TemplateYamlPersistence(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WriteYaml_CreatesFileInGlobalScope()
    {
        var template = new ContainerTemplate
        {
            Code = "test-global",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };

        await _persistence.WriteYamlAsync(template, "code: test-global");

        var path = _persistence.GetYamlPath(template);
        File.Exists(path).Should().BeTrue();
        path.Should().Contain(Path.Combine("global", "test-global.yaml"));
        (await File.ReadAllTextAsync(path)).Should().Be("code: test-global");
    }

    [Fact]
    public async Task WriteYaml_CreatesFileInOrganizationScope()
    {
        var orgId = Guid.NewGuid();
        var template = new ContainerTemplate
        {
            Code = "test-org",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Organization,
            OrganizationId = orgId
        };

        await _persistence.WriteYamlAsync(template, "code: test-org");

        var path = _persistence.GetYamlPath(template);
        path.Should().Contain(Path.Combine("organization", orgId.ToString()));
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task WriteYaml_CreatesFileInTeamScope()
    {
        var teamId = Guid.NewGuid();
        var template = new ContainerTemplate
        {
            Code = "test-team",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Team,
            TeamId = teamId
        };

        await _persistence.WriteYamlAsync(template, "code: test-team");

        var path = _persistence.GetYamlPath(template);
        path.Should().Contain(Path.Combine("team", teamId.ToString()));
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task WriteYaml_CreatesFileInUserScope()
    {
        var template = new ContainerTemplate
        {
            Code = "test-user",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.User,
            OwnerId = "user-123"
        };

        await _persistence.WriteYamlAsync(template, "code: test-user");

        var path = _persistence.GetYamlPath(template);
        path.Should().Contain(Path.Combine("user", "user-123"));
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task WriteYaml_OverwritesExistingFile()
    {
        var template = new ContainerTemplate
        {
            Code = "overwrite",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };

        await _persistence.WriteYamlAsync(template, "version: 1.0.0");
        await _persistence.WriteYamlAsync(template, "version: 2.0.0");

        var content = await File.ReadAllTextAsync(_persistence.GetYamlPath(template));
        content.Should().Be("version: 2.0.0");
    }

    [Fact]
    public async Task WriteYaml_AtomicWrite_NoTempFileLeftOver()
    {
        var template = new ContainerTemplate
        {
            Code = "atomic",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };

        await _persistence.WriteYamlAsync(template, "code: atomic");

        var dir = Path.GetDirectoryName(_persistence.GetYamlPath(template))!;
        var tmpFiles = Directory.GetFiles(dir, "*.tmp");
        tmpFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadYaml_ExistingFile_ReturnsContent()
    {
        var template = new ContainerTemplate
        {
            Code = "read-test",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };

        await _persistence.WriteYamlAsync(template, "code: read-test\nversion: 1.0.0");

        var content = await _persistence.ReadYamlAsync(template);
        content.Should().Be("code: read-test\nversion: 1.0.0");
    }

    [Fact]
    public async Task ReadYaml_NonExistent_ReturnsNull()
    {
        var template = new ContainerTemplate
        {
            Code = "nonexistent",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };

        var content = await _persistence.ReadYamlAsync(template);
        content.Should().BeNull();
    }
}

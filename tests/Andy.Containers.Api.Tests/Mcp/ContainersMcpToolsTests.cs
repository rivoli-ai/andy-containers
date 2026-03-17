using Andy.Containers.Api.Mcp;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Mcp;

public class ContainersMcpToolsTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly ContainersMcpTools _tools;

    public ContainersMcpToolsTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        var mockValidator = new Mock<ITemplateValidator>();
        _tools = new ContainersMcpTools(_db, mockValidator.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedTemplateAndProvider()
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            IsPublished = true
        };
        var provider = new InfrastructureProvider
        {
            Code = "docker-local",
            Name = "Local Docker",
            Type = ProviderType.Docker
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return (template, provider);
    }

    [Fact]
    public async Task ListContainers_NoContainers_ShouldReturnEmpty()
    {
        var result = await _tools.ListContainers();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListContainers_WithContainers_ShouldReturnAll()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _db.Containers.AddRange(
            new Container { Name = "c1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Running },
            new Container { Name = "c2", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Stopped }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListContainers();

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListContainers_WithStatusFilter_ShouldFilterCorrectly()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _db.Containers.AddRange(
            new Container { Name = "running1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Running },
            new Container { Name = "stopped1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id, Status = ContainerStatus.Stopped }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListContainers(status: "Running");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("running1");
        result[0].Status.Should().Be("Running");
    }

    [Fact]
    public async Task GetContainer_ExistingId_ShouldReturnDetail()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "detail-test",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            IdeEndpoint = "https://ide.test.com"
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _tools.GetContainer(container.Id.ToString());

        result.Should().NotBeNull();
        result!.Name.Should().Be("detail-test");
        result.IdeEndpoint.Should().Be("https://ide.test.com");
        result.Status.Should().Be("Running");
        result.TemplateName.Should().Be("Full Stack");
        result.ProviderName.Should().Be("Local Docker");
    }

    [Fact]
    public async Task GetContainer_InvalidGuid_ShouldReturnNull()
    {
        var result = await _tools.GetContainer("not-a-guid");

        result.Should().BeNull();
    }

    [Fact]
    public async Task BrowseTemplates_ShouldReturnPublishedOnly()
    {
        _db.Templates.AddRange(
            new ContainerTemplate { Code = "pub", Name = "Published", Version = "1.0", BaseImage = "img", IsPublished = true },
            new ContainerTemplate { Code = "unpub", Name = "Unpublished", Version = "1.0", BaseImage = "img", IsPublished = false }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.BrowseTemplates();

        result.Should().HaveCount(1);
        result[0].Code.Should().Be("pub");
    }

    [Fact]
    public async Task ListProviders_ShouldReturnAll()
    {
        _db.Providers.AddRange(
            new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, Region = "local" },
            new InfrastructureProvider { Code = "apple", Name = "Apple", Type = ProviderType.AppleContainer }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListProviders();

        result.Should().HaveCount(2);
        result.Should().Contain(p => p.Code == "docker");
        result.Should().Contain(p => p.Code == "apple");
    }

    [Fact]
    public async Task ListWorkspaces_ShouldReturnAll()
    {
        _db.Workspaces.AddRange(
            new Workspace { Name = "WS1", OwnerId = "user1", GitRepositoryUrl = "https://github.com/test/a" },
            new Workspace { Name = "WS2", OwnerId = "user2" }
        );
        await _db.SaveChangesAsync();

        var result = await _tools.ListWorkspaces();

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListImages_WithExistingTemplate_ShouldReturnImages()
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        _db.Images.Add(new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = "sha256:abc",
            Tag = "full-stack:1.0.0-1",
            ImageReference = "registry/full-stack:1.0.0-1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = 1,
            BuildStatus = ImageBuildStatus.Succeeded
        });
        await _db.SaveChangesAsync();

        var result = await _tools.ListImages("full-stack");

        result.Should().HaveCount(1);
        result[0].Tag.Should().Be("full-stack:1.0.0-1");
        result[0].BuildStatus.Should().Be("Succeeded");
    }

    [Fact]
    public async Task ListImages_NonExistentTemplate_ShouldReturnEmpty()
    {
        var result = await _tools.ListImages("nonexistent");

        result.Should().BeEmpty();
    }

    // --- YAML MCP Tool Tests ---

    [Fact]
    public async Task ValidateTemplateYaml_ValidYaml_ShouldReturnValid()
    {
        var mockValidator = new Mock<ITemplateValidator>();
        mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        var tools = new ContainersMcpTools(_db, mockValidator.Object);

        var result = await tools.ValidateTemplateYaml("code: test\nname: Test\nversion: 1.0.0\nbase_image: ubuntu:24.04");

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateTemplateYaml_InvalidYaml_ShouldReturnErrors()
    {
        var mockValidator = new Mock<ITemplateValidator>();
        var invalidResult = new TemplateValidationResult { Valid = false };
        invalidResult.Errors.Add(new TemplateValidationError { Field = "code", Message = "Field 'code' is required" });
        invalidResult.Warnings.Add(new TemplateValidationWarning { Field = "ports", Message = "Port 8080 not declared" });
        mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidResult);
        var tools = new ContainersMcpTools(_db, mockValidator.Object);

        var result = await tools.ValidateTemplateYaml("name: test");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Should().Contain("code");
        result.Warnings.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetTemplateDefinition_ExistingTemplate_ShouldReturnYaml()
    {
        _db.Templates.Add(new ContainerTemplate
        {
            Code = "yaml-def",
            Name = "YAML Def Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            IsPublished = true
        });
        await _db.SaveChangesAsync();

        var result = await _tools.GetTemplateDefinition("yaml-def");

        result.Should().NotBeNull();
        result!.Code.Should().Be("yaml-def");
        result.Yaml.Should().Contain("code: yaml-def");
        result.Yaml.Should().Contain("version: 1.0.0");
    }

    [Fact]
    public async Task GetTemplateDefinition_NonExistent_ShouldReturnNull()
    {
        var result = await _tools.GetTemplateDefinition("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateTemplateFromYaml_ValidYaml_ShouldCreateAndReturnSuccess()
    {
        var mockValidator = new Mock<ITemplateValidator>();
        mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerTemplate
            {
                Code = "mcp-created",
                Name = "MCP Created",
                Version = "1.0.0",
                BaseImage = "ubuntu:24.04"
            });
        var tools = new ContainersMcpTools(_db, mockValidator.Object);

        var result = await tools.CreateTemplateFromYaml("code: mcp-created\nname: MCP Created\nversion: 1.0.0\nbase_image: ubuntu:24.04");

        result.Success.Should().BeTrue();
        result.Code.Should().Be("mcp-created");
        result.TemplateId.Should().NotBeNull();
        result.Errors.Should().BeEmpty();

        var saved = await _db.Templates.FindAsync(result.TemplateId);
        saved.Should().NotBeNull();
        saved!.Code.Should().Be("mcp-created");
    }

    [Fact]
    public async Task CreateTemplateFromYaml_InvalidYaml_ShouldReturnErrors()
    {
        var mockValidator = new Mock<ITemplateValidator>();
        var invalidResult = new TemplateValidationResult { Valid = false };
        invalidResult.Errors.Add(new TemplateValidationError { Field = "version", Message = "Invalid semver" });
        mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidResult);
        var tools = new ContainersMcpTools(_db, mockValidator.Object);

        var result = await tools.CreateTemplateFromYaml("code: bad\nversion: xyz");

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.TemplateId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateTemplateDefinition_ValidYaml_ShouldUpdateTemplate()
    {
        var template = new ContainerTemplate
        {
            Code = "mcp-update",
            Name = "Original",
            Version = "1.0.0",
            BaseImage = "ubuntu:22.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var mockValidator = new Mock<ITemplateValidator>();
        mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerTemplate
            {
                Code = "mcp-update",
                Name = "Updated",
                Version = "2.0.0",
                BaseImage = "ubuntu:24.04"
            });
        var tools = new ContainersMcpTools(_db, mockValidator.Object);

        var result = await tools.UpdateTemplateDefinition(template.Id.ToString(),
            "code: mcp-update\nname: Updated\nversion: 2.0.0\nbase_image: ubuntu:24.04");

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();

        var updated = await _db.Templates.FindAsync(template.Id);
        updated!.Name.Should().Be("Updated");
        updated.Version.Should().Be("2.0.0");
    }

    [Fact]
    public async Task UpdateTemplateDefinition_InvalidId_ShouldReturnError()
    {
        var result = await _tools.UpdateTemplateDefinition("not-a-guid", "code: test");

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain("Invalid template ID");
    }

    [Fact]
    public async Task UpdateTemplateDefinition_NonExistent_ShouldReturnError()
    {
        var result = await _tools.UpdateTemplateDefinition(Guid.NewGuid().ToString(), "code: test");

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain("Template not found");
    }

    [Fact]
    public async Task UpdateTemplateDefinition_InvalidYaml_ShouldReturnErrors()
    {
        var template = new ContainerTemplate
        {
            Code = "mcp-upd-bad",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var mockValidator = new Mock<ITemplateValidator>();
        var invalidResult = new TemplateValidationResult { Valid = false };
        invalidResult.Errors.Add(new TemplateValidationError { Field = "version", Message = "Invalid semver" });
        mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidResult);
        var tools = new ContainersMcpTools(_db, mockValidator.Object);

        var result = await tools.UpdateTemplateDefinition(template.Id.ToString(), "code: test\nversion: bad");

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }
}

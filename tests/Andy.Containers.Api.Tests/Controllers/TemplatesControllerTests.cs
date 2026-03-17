using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class TemplatesControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<ITemplateValidator> _mockValidator;
    private readonly TemplatesController _controller;

    public TemplatesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        _mockValidator = new Mock<ITemplateValidator>();
        _controller = new TemplatesController(_db, mockEnv.Object, _mockCurrentUser.Object, _mockValidator.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedTemplate()
    {
        var template = new ContainerTemplate
        {
            Code = "dotnet",
            Name = ".NET Dev",
            Version = "1.0.0",
            BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
            IsPublished = true
        };

        var result = await _controller.Create(template, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var returned = created.Value.Should().BeOfType<ContainerTemplate>().Subject;
        returned.Code.Should().Be("dotnet");
        returned.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Get_ExistingTemplate_ShouldReturnOk()
    {
        var template = new ContainerTemplate
        {
            Code = "python",
            Name = "Python Dev",
            Version = "1.0.0",
            BaseImage = "python:3.12"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.Get(template.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<ContainerTemplate>().Subject;
        returned.Code.Should().Be("python");
    }

    [Fact]
    public async Task Get_NonExistentTemplate_ShouldReturnNotFound()
    {
        var result = await _controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetByCode_ExistingCode_ShouldReturnOk()
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "2.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.GetByCode("full-stack", CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<ContainerTemplate>().Subject;
        returned.Code.Should().Be("full-stack");
    }

    [Fact]
    public async Task GetByCode_NonExistentCode_ShouldReturnNotFound()
    {
        var result = await _controller.GetByCode("nonexistent", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task List_ShouldReturnOnlyPublishedTemplates()
    {
        _db.Templates.AddRange(
            new ContainerTemplate { Code = "pub1", Name = "Published", Version = "1.0", BaseImage = "img", IsPublished = true },
            new ContainerTemplate { Code = "unpub1", Name = "Unpublished", Version = "1.0", BaseImage = "img", IsPublished = false }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(scope: null, organizationId: null, teamId: null,
            search: null, gpuRequired: null, ideType: null);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value!;
        var totalCount = (int)value.GetType().GetProperty("totalCount")!.GetValue(value)!;
        totalCount.Should().Be(1);
    }

    [Fact]
    public async Task List_SearchByTag_ShouldReturnAllMatchingTemplates()
    {
        _db.Templates.AddRange(
            new ContainerTemplate { Code = "full-stack", Name = "Full Stack", Version = "1.0", BaseImage = "img", IsPublished = true, Tags = ["dotnet", "python", "node"] },
            new ContainerTemplate { Code = "dotnet-8", Name = ".NET 8 Dev", Version = "1.0", BaseImage = "img", IsPublished = true, Tags = ["dotnet"] },
            new ContainerTemplate { Code = "andy-cli", Name = "Andy CLI", Version = "1.0", BaseImage = "img", IsPublished = true, Tags = ["andy-cli", "dotnet", "ai"] },
            new ContainerTemplate { Code = "python-only", Name = "Python Only", Version = "1.0", BaseImage = "img", IsPublished = true, Tags = ["python"] }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(scope: null, organizationId: null, teamId: null,
            search: "dotnet", gpuRequired: null, ideType: null);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value!;
        var totalCount = (int)value.GetType().GetProperty("totalCount")!.GetValue(value)!;
        totalCount.Should().Be(3, "3 templates are tagged with 'dotnet'");
    }

    [Fact]
    public async Task Publish_ShouldSetIsPublishedTrue()
    {
        var template = new ContainerTemplate
        {
            Code = "unpub",
            Name = "Unpub",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            IsPublished = false
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.Publish(template.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var updated = await _db.Templates.FindAsync(template.Id);
        updated!.IsPublished.Should().BeTrue();
        updated.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_ExistingTemplate_ShouldRemoveAndReturnNoContent()
    {
        var template = new ContainerTemplate
        {
            Code = "to-delete",
            Name = "Delete Me",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.Delete(template.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var found = await _db.Templates.FindAsync(template.Id);
        found.Should().BeNull();
    }

    // --- YAML Endpoint Tests ---

    [Fact]
    public async Task Validate_ValidYaml_ShouldReturnOkWithIsValid()
    {
        var validResult = new TemplateValidationResult { Valid = true };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validResult);

        var result = await _controller.Validate(new ValidateYamlRequest { Yaml = "code: test" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<TemplateValidationResult>().Subject;
        returned.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_InvalidYaml_ShouldReturnOkWithErrors()
    {
        var invalidResult = new TemplateValidationResult { Valid = false };
        invalidResult.Errors.Add(new TemplateValidationError { Field = "code", Message = "Field 'code' is required" });
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidResult);

        var result = await _controller.Validate(new ValidateYamlRequest { Yaml = "name: test" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<TemplateValidationResult>().Subject;
        returned.IsValid.Should().BeFalse();
        returned.Errors.Should().HaveCount(1);
    }

    [Fact]
    public async Task Validate_EmptyYaml_ShouldReturnBadRequest()
    {
        var result = await _controller.Validate(new ValidateYamlRequest { Yaml = "  " }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetDefinition_ExistingTemplate_ShouldReturnSyntheticYaml()
    {
        var template = new ContainerTemplate
        {
            Code = "test-def",
            Name = "Test Def",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.GetDefinition(template.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        var content = value.GetType().GetProperty("content")!.GetValue(value) as string;
        content.Should().Contain("code: test-def");
        content.Should().Contain("base_image: ubuntu:24.04");
    }

    [Fact]
    public async Task GetDefinition_NonExistent_ShouldReturnNotFound()
    {
        var result = await _controller.GetDefinition(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateDefinition_ValidYaml_ShouldUpdateTemplate()
    {
        var template = new ContainerTemplate
        {
            Code = "upd-def",
            Name = "Original",
            Version = "1.0.0",
            BaseImage = "ubuntu:22.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var parsedTemplate = new ContainerTemplate
        {
            Code = "upd-def",
            Name = "Updated Name",
            Version = "2.0.0",
            BaseImage = "ubuntu:24.04",
            Description = "Updated desc",
            Tags = ["new-tag"]
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        var result = await _controller.UpdateDefinition(template.Id,
            new UpdateDefinitionRequest { Yaml = "code: upd-def\nname: Updated Name\nversion: 2.0.0\nbase_image: ubuntu:24.04" },
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<ContainerTemplate>().Subject;
        returned.Name.Should().Be("Updated Name");
        returned.Version.Should().Be("2.0.0");
        returned.BaseImage.Should().Be("ubuntu:24.04");
    }

    [Fact]
    public async Task UpdateDefinition_InvalidYaml_ShouldReturn422()
    {
        var template = new ContainerTemplate
        {
            Code = "upd-invalid",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var invalidResult = new TemplateValidationResult { Valid = false };
        invalidResult.Errors.Add(new TemplateValidationError { Field = "version", Message = "Invalid semver" });
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidResult);

        var result = await _controller.UpdateDefinition(template.Id,
            new UpdateDefinitionRequest { Yaml = "code: test\nversion: bad" },
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task UpdateDefinition_NonExistent_ShouldReturnNotFound()
    {
        var result = await _controller.UpdateDefinition(Guid.NewGuid(),
            new UpdateDefinitionRequest { Yaml = "code: test" },
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateDefinition_EmptyYaml_ShouldReturnBadRequest()
    {
        var template = new ContainerTemplate
        {
            Code = "upd-empty",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.UpdateDefinition(template.Id,
            new UpdateDefinitionRequest { Yaml = "" },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateDefinition_NonOwnerNonAdmin_ShouldReturnForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("other-user");

        var template = new ContainerTemplate
        {
            Code = "upd-forbid",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "owner-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _controller.UpdateDefinition(template.Id,
            new UpdateDefinitionRequest { Yaml = "code: test" },
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CreateFromYaml_ValidYaml_ShouldReturnCreated()
    {
        var parsedTemplate = new ContainerTemplate
        {
            Code = "from-yaml",
            Name = "From YAML",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        var result = await _controller.CreateFromYaml(
            new ValidateYamlRequest { Yaml = "code: from-yaml\nname: From YAML\nversion: 1.0.0\nbase_image: ubuntu:24.04" },
            CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var returned = created.Value.Should().BeOfType<ContainerTemplate>().Subject;
        returned.Code.Should().Be("from-yaml");
        returned.OwnerId.Should().Be("test-user");
    }

    [Fact]
    public async Task CreateFromYaml_InvalidYaml_ShouldReturn422()
    {
        var invalidResult = new TemplateValidationResult { Valid = false };
        invalidResult.Errors.Add(new TemplateValidationError { Field = "code", Message = "Required" });
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidResult);

        var result = await _controller.CreateFromYaml(
            new ValidateYamlRequest { Yaml = "name: test" },
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task CreateFromYaml_EmptyYaml_ShouldReturnBadRequest()
    {
        var result = await _controller.CreateFromYaml(
            new ValidateYamlRequest { Yaml = "" },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateFromYaml_GlobalScope_NonAdmin_ShouldReturnForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);

        var parsedTemplate = new ContainerTemplate
        {
            Code = "global-test",
            Name = "Global",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        var result = await _controller.CreateFromYaml(
            new ValidateYamlRequest { Yaml = "code: global-test\ncatalog_scope: global" },
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }
}

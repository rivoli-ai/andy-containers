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
    private readonly Mock<ITemplateYamlPersistence> _mockYamlPersistence;
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
        _mockYamlPersistence = new Mock<ITemplateYamlPersistence>();
        _controller = new TemplatesController(_db, mockEnv.Object, _mockCurrentUser.Object, _mockValidator.Object, _mockYamlPersistence.Object);
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

    // --- Validation on existing endpoints ---

    [Fact]
    public async Task Create_MissingCode_ShouldReturn422()
    {
        var template = new ContainerTemplate
        {
            Code = "ab", // too short
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        var result = await _controller.Create(template, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task Create_MissingVersion_ShouldReturn422()
    {
        var template = new ContainerTemplate
        {
            Code = "valid-code",
            Name = "Test",
            Version = "",
            BaseImage = "ubuntu:24.04"
        };

        var result = await _controller.Create(template, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task Create_GlobalScope_NonAdmin_ShouldReturnForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        var template = new ContainerTemplate
        {
            Code = "global-test",
            Name = "Global",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            CatalogScope = CatalogScope.Global
        };

        var result = await _controller.Create(template, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Update_InvalidFields_ShouldReturn422()
    {
        var template = new ContainerTemplate
        {
            Code = "upd-val",
            Name = "Original",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var update = new ContainerTemplate
        {
            Code = "upd-val",
            Name = "",  // invalid: empty
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        var result = await _controller.Update(template.Id, update, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    // --- Dependencies Endpoint Tests ---

    [Fact]
    public async Task UpdateDependencies_ValidDeps_ShouldReplaceAll()
    {
        var template = new ContainerTemplate
        {
            Code = "dep-test",
            Name = "Dep Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        _db.DependencySpecs.Add(new DependencySpec
        {
            Name = "old-dep",
            VersionConstraint = "1.0.0",
            TemplateId = template.Id,
            Type = DependencyType.Tool
        });
        await _db.SaveChangesAsync();

        var newDeps = new[]
        {
            new DependencySpec { Name = "dotnet-sdk", VersionConstraint = "8.0.*", Type = DependencyType.Sdk },
            new DependencySpec { Name = "python", VersionConstraint = ">=3.12,<4.0", Type = DependencyType.Runtime }
        };

        var result = await _controller.UpdateDependencies(template.Id, newDeps, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var deps = _db.DependencySpecs.Where(d => d.TemplateId == template.Id).ToList();
        deps.Should().HaveCount(2);
        deps.Should().Contain(d => d.Name == "dotnet-sdk");
        deps.Should().Contain(d => d.Name == "python");
        deps.Should().NotContain(d => d.Name == "old-dep");
    }

    [Fact]
    public async Task UpdateDependencies_DuplicateName_ShouldReturn422()
    {
        var template = new ContainerTemplate
        {
            Code = "dep-dup",
            Name = "Dep Dup",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var newDeps = new[]
        {
            new DependencySpec { Name = "node", VersionConstraint = "20.x", Type = DependencyType.Runtime },
            new DependencySpec { Name = "node", VersionConstraint = "18.x", Type = DependencyType.Runtime }
        };

        var result = await _controller.UpdateDependencies(template.Id, newDeps, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task UpdateDependencies_InvalidConstraint_ShouldReturn422()
    {
        var template = new ContainerTemplate
        {
            Code = "dep-bad",
            Name = "Dep Bad",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var newDeps = new[]
        {
            new DependencySpec { Name = "node", VersionConstraint = "8.0.*.1", Type = DependencyType.Runtime }
        };

        var result = await _controller.UpdateDependencies(template.Id, newDeps, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task UpdateDependencies_NonExistent_ShouldReturnNotFound()
    {
        var result = await _controller.UpdateDependencies(Guid.NewGuid(),
            [new DependencySpec { Name = "test", VersionConstraint = "1.0.0", Type = DependencyType.Tool }],
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
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

    // === Story #6: Template Audit Events ===

    [Fact]
    public async Task Create_EmitsCreatedEvent()
    {
        var template = new ContainerTemplate
        {
            Code = "audit-create",
            Name = "Audit Create",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        await _controller.Create(template, CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == template.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.Created);
        var evt = events[0];
        evt.SubjectId.Should().Be("test-user");
        evt.AfterSnapshot.Should().Contain("audit-create");
        evt.BeforeSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task Update_EmitsUpdatedEventWithBeforeAfterSnapshots()
    {
        var template = new ContainerTemplate
        {
            Code = "audit-update",
            Name = "Original",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var update = new ContainerTemplate
        {
            Code = "audit-update",
            Name = "Updated",
            Version = "2.0.0",
            BaseImage = "ubuntu:24.04"
        };

        await _controller.Update(template.Id, update, CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == template.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.Updated);
        var evt = events.First(e => e.EventType == TemplateEventType.Updated);
        evt.BeforeSnapshot.Should().Contain("Original");
        evt.AfterSnapshot.Should().Contain("Updated");
    }

    [Fact]
    public async Task Delete_EmitsDeletedEventWithBeforeSnapshot()
    {
        var template = new ContainerTemplate
        {
            Code = "audit-delete",
            Name = "To Delete",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        await _controller.Delete(template.Id, CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == template.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.Deleted);
        var evt = events[0];
        evt.BeforeSnapshot.Should().Contain("audit-delete");
        evt.AfterSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task Publish_EmitsPublishedEvent()
    {
        var template = new ContainerTemplate
        {
            Code = "audit-publish",
            Name = "To Publish",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user",
            IsPublished = false
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        await _controller.Publish(template.Id, CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == template.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.Published);
    }

    [Fact]
    public async Task CreateFromYaml_EmitsCreatedEvent()
    {
        var parsedTemplate = new ContainerTemplate
        {
            Code = "yaml-audit",
            Name = "From YAML Audit",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        await _controller.CreateFromYaml(
            new ValidateYamlRequest { Yaml = "code: yaml-audit\nname: From YAML Audit\nversion: 1.0.0\nbase_image: ubuntu:24.04" },
            CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == parsedTemplate.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.Created);
    }

    [Fact]
    public async Task UpdateDefinition_EmitsDefinitionUpdatedEvent()
    {
        var template = new ContainerTemplate
        {
            Code = "def-audit",
            Name = "Def Audit",
            Version = "1.0.0",
            BaseImage = "ubuntu:22.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var parsedTemplate = new ContainerTemplate
        {
            Code = "def-audit",
            Name = "Updated Def",
            Version = "2.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        await _controller.UpdateDefinition(template.Id,
            new UpdateDefinitionRequest { Yaml = "code: def-audit\nversion: 2.0.0" },
            CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == template.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.DefinitionUpdated);
        var evt = events[0];
        evt.BeforeSnapshot.Should().Contain("1.0.0");
        evt.AfterSnapshot.Should().Contain("2.0.0");
    }

    [Fact]
    public async Task UpdateDependencies_EmitsDependenciesUpdatedEvent()
    {
        var template = new ContainerTemplate
        {
            Code = "dep-audit",
            Name = "Dep Audit",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var newDeps = new[]
        {
            new DependencySpec { Name = "dotnet-sdk", VersionConstraint = "8.0.*", Type = DependencyType.Sdk }
        };

        await _controller.UpdateDependencies(template.Id, newDeps, CancellationToken.None);

        var events = _db.TemplateEvents.Where(e => e.TemplateId == template.Id).ToList();
        events.Should().ContainSingle(e => e.EventType == TemplateEventType.DependenciesUpdated);
        var evt = events[0];
        evt.AfterSnapshot.Should().Contain("dotnet-sdk");
    }

    [Fact]
    public async Task GetEvents_ReturnsEventsForTemplate()
    {
        var template = new ContainerTemplate
        {
            Code = "events-query",
            Name = "Events Query",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        _db.TemplateEvents.Add(new TemplateEvent
        {
            TemplateId = template.Id,
            EventType = TemplateEventType.Created,
            SubjectId = "test-user",
            AfterSnapshot = "{}"
        });
        _db.TemplateEvents.Add(new TemplateEvent
        {
            TemplateId = template.Id,
            EventType = TemplateEventType.Updated,
            SubjectId = "test-user",
            BeforeSnapshot = "{}",
            AfterSnapshot = "{}"
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetEvents(template.Id, ct: CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var events = ok.Value.Should().BeAssignableTo<List<TemplateEvent>>().Subject;
        events.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEvents_NonExistent_ReturnsNotFound()
    {
        var result = await _controller.GetEvents(Guid.NewGuid(), ct: CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // === Story #7: YAML Persistence ===

    [Fact]
    public async Task CreateFromYaml_CallsYamlPersistence()
    {
        var parsedTemplate = new ContainerTemplate
        {
            Code = "persist-create",
            Name = "Persist Create",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        var yaml = "code: persist-create\nname: Persist Create\nversion: 1.0.0\nbase_image: ubuntu:24.04";
        await _controller.CreateFromYaml(new ValidateYamlRequest { Yaml = yaml }, CancellationToken.None);

        _mockYamlPersistence.Verify(p => p.WriteYamlAsync(
            It.Is<ContainerTemplate>(t => t.Code == "persist-create"),
            yaml,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDefinition_CallsYamlPersistence()
    {
        var template = new ContainerTemplate
        {
            Code = "persist-update",
            Name = "Persist Update",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var parsedTemplate = new ContainerTemplate
        {
            Code = "persist-update",
            Name = "Updated",
            Version = "2.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedTemplate);

        var yaml = "code: persist-update\nversion: 2.0.0";
        await _controller.UpdateDefinition(template.Id, new UpdateDefinitionRequest { Yaml = yaml }, CancellationToken.None);

        _mockYamlPersistence.Verify(p => p.WriteYamlAsync(
            It.IsAny<ContainerTemplate>(),
            yaml,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDefinition_ReturnsPersistedYamlWhenAvailable()
    {
        var template = new ContainerTemplate
        {
            Code = "persisted-def",
            Name = "Persisted Def",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        _mockYamlPersistence.Setup(p => p.ReadYamlAsync(It.IsAny<ContainerTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("code: persisted-def\nname: Persisted Def\nversion: 1.0.0\nbase_image: ubuntu:24.04");

        var result = await _controller.GetDefinition(template.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        var content = value.GetType().GetProperty("content")!.GetValue(value) as string;
        content.Should().Contain("code: persisted-def");
    }

    // === Story #8: OCI Image Reference Validation ===

    [Fact]
    public async Task Create_InvalidOciImage_Returns422()
    {
        var template = new ContainerTemplate
        {
            Code = "bad-image",
            Name = "Bad Image",
            Version = "1.0.0",
            BaseImage = "not a valid image!"
        };

        var result = await _controller.Create(template, CancellationToken.None);

        var unprocessable = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var validation = unprocessable.Value.Should().BeOfType<TemplateValidationResult>().Subject;
        validation.Errors.Should().Contain(e => e.Field == "base_image" && e.Message.Contains("OCI"));
    }

    [Fact]
    public async Task Create_ValidOciImage_Succeeds()
    {
        var template = new ContainerTemplate
        {
            Code = "good-image",
            Name = "Good Image",
            Version = "1.0.0",
            BaseImage = "ghcr.io/org/image:1.0"
        };

        var result = await _controller.Create(template, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }
}

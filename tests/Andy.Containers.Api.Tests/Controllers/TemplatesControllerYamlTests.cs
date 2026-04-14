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

public class TemplatesControllerYamlTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IYamlTemplateParser> _mockParser;
    private readonly TemplatesController _controller;

    private const string ValidYaml = """
        code: test-template
        name: Test Template
        version: 1.0.0
        base_image: ubuntu:24.04
        """;

    public TemplatesControllerYamlTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _mockParser = new Mock<IYamlTemplateParser>();
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var mockBuildService = new Mock<ITemplateBuildService>();
        _controller = new TemplatesController(_db, mockEnv.Object, _mockCurrentUser.Object, _mockParser.Object, mockOrgMembership.Object, mockBuildService.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public void Validate_ValidYaml_ReturnsOkWithIsValidTrue()
    {
        var validResult = new YamlValidationResult { IsValid = true };
        _mockParser.Setup(p => p.Validate(ValidYaml)).Returns(validResult);

        var result = _controller.Validate(new YamlContentRequest(ValidYaml));

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<YamlValidationResult>().Subject;
        validation.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_InvalidYaml_ReturnsOkWithIsValidFalseAndErrors()
    {
        var invalidResult = new YamlValidationResult
        {
            IsValid = false,
            Errors = [new YamlValidationError { Field = "code", Message = "'code' is required" }]
        };
        _mockParser.Setup(p => p.Validate("bad yaml")).Returns(invalidResult);

        var result = _controller.Validate(new YamlContentRequest("bad yaml"));

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<YamlValidationResult>().Subject;
        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateFromYaml_ValidYaml_CreatesTemplateAndReturns201()
    {
        var validResult = new YamlValidationResult { IsValid = true };
        _mockParser.Setup(p => p.Validate(ValidYaml)).Returns(validResult);
        _mockParser.Setup(p => p.Parse(ValidYaml)).Returns(new ContainerTemplate
        {
            Code = "test-template",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        });

        var result = await _controller.CreateFromYaml(new YamlContentRequest(ValidYaml), CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var template = created.Value.Should().BeOfType<ContainerTemplate>().Subject;
        template.Code.Should().Be("test-template");
        template.OwnerId.Should().Be("test-user");
    }

    [Fact]
    public async Task CreateFromYaml_InvalidYaml_ReturnsBadRequest()
    {
        var invalidResult = new YamlValidationResult
        {
            IsValid = false,
            Errors = [new YamlValidationError { Field = "code", Message = "'code' is required" }]
        };
        _mockParser.Setup(p => p.Validate("bad")).Returns(invalidResult);

        var result = await _controller.CreateFromYaml(new YamlContentRequest("bad"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateDefinition_ValidYaml_UpdatesTemplate()
    {
        var existing = new ContainerTemplate
        {
            Code = "existing",
            Name = "Existing",
            Version = "1.0.0",
            BaseImage = "ubuntu:22.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(existing);
        await _db.SaveChangesAsync();

        var validResult = new YamlValidationResult { IsValid = true };
        _mockParser.Setup(p => p.Validate(ValidYaml)).Returns(validResult);
        _mockParser.Setup(p => p.Parse(ValidYaml)).Returns(new ContainerTemplate
        {
            Code = "existing",
            Name = "Updated Name",
            Version = "2.0.0",
            BaseImage = "ubuntu:24.04"
        });

        var result = await _controller.UpdateDefinition(existing.Id, new YamlContentRequest(ValidYaml), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var template = ok.Value.Should().BeOfType<ContainerTemplate>().Subject;
        template.Name.Should().Be("Updated Name");
        template.Version.Should().Be("2.0.0");
        template.BaseImage.Should().Be("ubuntu:24.04");
        template.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateDefinition_NonexistentTemplate_ReturnsNotFound()
    {
        var result = await _controller.UpdateDefinition(Guid.NewGuid(), new YamlContentRequest(ValidYaml), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateDefinition_InvalidYaml_ReturnsBadRequest()
    {
        var existing = new ContainerTemplate
        {
            Code = "existing",
            Name = "Existing",
            Version = "1.0.0",
            BaseImage = "ubuntu:22.04",
            OwnerId = "test-user"
        };
        _db.Templates.Add(existing);
        await _db.SaveChangesAsync();

        var invalidResult = new YamlValidationResult
        {
            IsValid = false,
            Errors = [new YamlValidationError { Field = "version", Message = "Invalid version" }]
        };
        _mockParser.Setup(p => p.Validate("bad yaml")).Returns(invalidResult);

        var result = await _controller.UpdateDefinition(existing.Id, new YamlContentRequest("bad yaml"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}

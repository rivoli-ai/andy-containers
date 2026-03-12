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
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        _controller = new TemplatesController(_db, mockEnv.Object, _mockCurrentUser.Object, mockOrgMembership.Object);
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
}

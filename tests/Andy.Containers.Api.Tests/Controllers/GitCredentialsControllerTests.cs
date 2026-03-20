using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class GitCredentialsControllerTests
{
    private readonly Mock<IGitCredentialService> _mockCredentialService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly GitCredentialsController _controller;

    public GitCredentialsControllerTests()
    {
        _mockCredentialService = new Mock<IGitCredentialService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _controller = new GitCredentialsController(_mockCredentialService.Object, _mockCurrentUser.Object);
    }

    [Fact]
    public async Task List_ShouldReturnCredentialDtosWithoutEncryptedToken()
    {
        var credentials = new List<GitCredential>
        {
            new() { Id = Guid.NewGuid(), OwnerId = "test-user", Label = "github", EncryptedToken = "encrypted-secret", GitHost = "github.com" },
            new() { Id = Guid.NewGuid(), OwnerId = "test-user", Label = "gitlab", EncryptedToken = "encrypted-secret-2", GitHost = "gitlab.com" }
        };
        _mockCredentialService.Setup(s => s.ListAsync("test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(credentials);

        var result = await _controller.List(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value.Should().BeAssignableTo<IEnumerable<GitCredentialDto>>().Subject.ToList();
        items.Should().HaveCount(2);
        items[0].Label.Should().Be("github");
        items[0].GitHost.Should().Be("github.com");
        items[1].Label.Should().Be("gitlab");
        // GitCredentialDto has no Token/EncryptedToken property — verify the DTO type has exactly the expected fields
        var dtoProperties = typeof(GitCredentialDto).GetProperties().Select(p => p.Name).ToHashSet();
        dtoProperties.Should().NotContain("Token");
        dtoProperties.Should().NotContain("EncryptedToken");
        dtoProperties.Should().BeEquivalentTo(new[] { "Id", "Label", "GitHost", "CredentialType", "CreatedAt", "LastUsedAt" });
    }

    [Fact]
    public async Task List_NoCredentials_ShouldReturnEmptyList()
    {
        _mockCredentialService.Setup(s => s.ListAsync("test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitCredential>());

        var result = await _controller.List(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value.Should().BeAssignableTo<IEnumerable<GitCredentialDto>>().Subject.ToList();
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedCredentialWithCorrectFields()
    {
        var id = Guid.NewGuid();
        var credential = new GitCredential
        {
            Id = id, OwnerId = "test-user", Label = "my-pat", EncryptedToken = "enc", GitHost = "github.com"
        };
        _mockCredentialService.Setup(s => s.CreateAsync("test-user", "my-pat", "token123", "github.com", GitCredentialType.PersonalAccessToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var dto = new CreateGitCredentialDto("my-pat", "token123", "github.com");
        var result = await _controller.Create(dto, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var returnedDto = created.Value.Should().BeOfType<GitCredentialDto>().Subject;
        returnedDto.Id.Should().Be(id);
        returnedDto.Label.Should().Be("my-pat");
        returnedDto.GitHost.Should().Be("github.com");
        returnedDto.CredentialType.Should().Be("PersonalAccessToken");
    }

    [Fact]
    public async Task Create_EmptyLabel_ShouldReturnBadRequest()
    {
        var dto = new CreateGitCredentialDto("", "token123");
        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_WhitespaceLabel_ShouldReturnBadRequest()
    {
        var dto = new CreateGitCredentialDto("   ", "token123");
        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_EmptyToken_ShouldReturnBadRequest()
    {
        var dto = new CreateGitCredentialDto("label", "");
        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_InvalidCredentialType_ShouldReturnBadRequest()
    {
        var dto = new CreateGitCredentialDto("label", "token123", CredentialType: "InvalidType");
        var result = await _controller.Create(dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_ValidCredentialType_ShouldParseAndCreate()
    {
        var credential = new GitCredential
        {
            Id = Guid.NewGuid(), OwnerId = "test-user", Label = "deploy", EncryptedToken = "enc",
            CredentialType = GitCredentialType.DeployKey
        };
        _mockCredentialService.Setup(s => s.CreateAsync("test-user", "deploy", "key-data", null, GitCredentialType.DeployKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var dto = new CreateGitCredentialDto("deploy", "key-data", CredentialType: "DeployKey");
        var result = await _controller.Create(dto, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var returnedDto = created.Value.Should().BeOfType<GitCredentialDto>().Subject;
        returnedDto.CredentialType.Should().Be("DeployKey");
    }

    [Fact]
    public async Task Delete_ExistingCredential_ShouldReturnNoContent()
    {
        var id = Guid.NewGuid();
        _mockCredentialService.Setup(s => s.DeleteAsync(id, "test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.Delete(id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _mockCredentialService.Verify(s => s.DeleteAsync(id, "test-user", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NonExistentCredential_ShouldReturnNotFound()
    {
        var id = Guid.NewGuid();
        _mockCredentialService.Setup(s => s.DeleteAsync(id, "test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.Delete(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_UsesCurrentUserAsOwner()
    {
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("specific-user-42");
        var credential = new GitCredential
        {
            Id = Guid.NewGuid(), OwnerId = "specific-user-42", Label = "cred", EncryptedToken = "enc"
        };
        _mockCredentialService.Setup(s => s.CreateAsync("specific-user-42", "cred", "tok", null, GitCredentialType.PersonalAccessToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var dto = new CreateGitCredentialDto("cred", "tok");
        await _controller.Create(dto, CancellationToken.None);

        _mockCredentialService.Verify(s => s.CreateAsync("specific-user-42", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<GitCredentialType>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

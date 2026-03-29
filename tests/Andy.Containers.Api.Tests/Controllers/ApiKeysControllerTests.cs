using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Net;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class ApiKeysControllerTests
{
    private readonly Mock<IApiKeyService> _mockService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership;
    private readonly ApiKeysController _controller;

    public ApiKeysControllerTests()
    {
        _mockService = new Mock<IApiKeyService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockOrgMembership = new Mock<IOrganizationMembershipService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");

        _controller = new ApiKeysController(_mockService.Object, _mockCurrentUser.Object, _mockOrgMembership.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("127.0.0.1") } }
        };
    }

    [Fact]
    public async Task Create_ReturnsCreatedWithDto()
    {
        var credential = new ApiKeyCredential
        {
            Id = Guid.NewGuid(),
            OwnerId = "test-user",
            Label = "my-key",
            Provider = ApiKeyProvider.Anthropic,
            EncryptedValue = "encrypted",
            EnvVarName = "ANTHROPIC_API_KEY",
            MaskedValue = "****...1234",
            IsValid = true,
            LastValidatedAt = DateTime.UtcNow
        };
        _mockService.Setup(s => s.CreateAsync("test-user", "my-key", ApiKeyProvider.Anthropic, "sk-test",
            null, null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var result = await _controller.Create(new CreateApiKeyDto { Label = "my-key", Provider = "Anthropic", ApiKey = "sk-test" }, default);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<ApiKeyDto>().Subject;
        dto.Label.Should().Be("my-key");
        dto.Provider.Should().Be("Anthropic");
        dto.MaskedValue.Should().Be("****...1234");
        dto.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Create_InvalidProvider_ReturnsBadRequest()
    {
        var result = await _controller.Create(new CreateApiKeyDto { Label = "x", Provider = "InvalidProvider", ApiKey = "k" }, default);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_ReturnsUserKeys()
    {
        var keys = new List<ApiKeyCredential>
        {
            new() { OwnerId = "test-user", Label = "k1", Provider = ApiKeyProvider.Anthropic, EncryptedValue = "e", EnvVarName = "ANTHROPIC_API_KEY", MaskedValue = "****...aaaa" },
            new() { OwnerId = "test-user", Label = "k2", Provider = ApiKeyProvider.OpenAI, EncryptedValue = "e", EnvVarName = "OPENAI_API_KEY", MaskedValue = "****...bbbb" }
        };
        _mockService.Setup(s => s.ListAsync("test-user", It.IsAny<CancellationToken>())).ReturnsAsync(keys);

        var result = await _controller.List(default);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeAssignableTo<IEnumerable<ApiKeyDto>>().Subject.ToList();
        dtos.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        _mockService.Setup(s => s.GetAsync(It.IsAny<Guid>(), "test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiKeyCredential?)null);

        var result = await _controller.Get(Guid.NewGuid(), default);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_ExistingKey_ReturnsNoContent()
    {
        _mockService.Setup(s => s.DeleteAsync(It.IsAny<Guid>(), "test-user", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.Delete(Guid.NewGuid(), default);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_NonExistentKey_ReturnsNotFound()
    {
        _mockService.Setup(s => s.DeleteAsync(It.IsAny<Guid>(), "test-user", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.Delete(Guid.NewGuid(), default);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Validate_ReturnsValidationResult()
    {
        _mockService.Setup(s => s.ValidateExistingAsync(It.IsAny<Guid>(), "test-user", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationResult(true));

        var result = await _controller.Validate(Guid.NewGuid(), default);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
    }

    [Fact]
    public async Task ListByOrg_AdminCanAccess()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var orgId = Guid.NewGuid();
        _mockService.Setup(s => s.ListByOrganizationAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ApiKeyCredential>());

        var result = await _controller.ListByOrganization(orgId, default);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ListByOrg_NonAdminWithoutPermission_ReturnsForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("test-user", It.IsAny<Guid>(), Permissions.ApiKeyAdmin, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.ListByOrganization(Guid.NewGuid(), default);
        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetHistory_ReturnsChangeEntries()
    {
        var entries = new List<ApiKeyChangeEntry>
        {
            new() { Action = "created", Timestamp = DateTime.UtcNow.ToString("O") },
            new() { Action = "validated", Timestamp = DateTime.UtcNow.ToString("O") }
        };
        _mockService.Setup(s => s.GetHistoryAsync(It.IsAny<Guid>(), "test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _controller.GetHistory(Guid.NewGuid(), default);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
    }

    [Fact]
    public async Task DtoNeverContainsRawKey()
    {
        var credential = new ApiKeyCredential
        {
            OwnerId = "test-user",
            Label = "test",
            Provider = ApiKeyProvider.Anthropic,
            EncryptedValue = "SHOULD-NOT-APPEAR",
            EnvVarName = "ANTHROPIC_API_KEY",
            MaskedValue = "****...test"
        };
        _mockService.Setup(s => s.GetAsync(It.IsAny<Guid>(), "test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var result = await _controller.Get(Guid.NewGuid(), default);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ApiKeyDto>().Subject;

        // DTO should not contain encrypted value or raw key
        var serialized = System.Text.Json.JsonSerializer.Serialize(dto);
        serialized.Should().NotContain("SHOULD-NOT-APPEAR");
    }
}

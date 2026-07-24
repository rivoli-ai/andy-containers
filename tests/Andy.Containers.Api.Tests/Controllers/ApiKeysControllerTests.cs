using System.Reflection;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public sealed class ApiKeysControllerTests
{
    private readonly Mock<IApiKeyService> _apiKeys = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly ApiKeysController _controller;

    public ApiKeysControllerTests()
    {
        _currentUser.Setup(u => u.GetUserId()).Returns("current-user");
        _controller = new ApiKeysController(_apiKeys.Object, _currentUser.Object);
    }

    [Fact]
    public async Task List_ReturnsOnlyCurrentUsersMaskedEntries()
    {
        _apiKeys
            .Setup(s => s.ListAsync("current-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Registration(
                    Guid.NewGuid(),
                    "current-user",
                    "openai",
                    "sk-••••••••abcd"),
            ]);

        var result = await _controller.List(CancellationToken.None);

        var entries = result.Should()
            .BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeAssignableTo<IEnumerable<ApiKeyEntry>>().Subject;
        entries.Should().ContainSingle()
            .Which.MaskedValue.Should().Be("sk-••••••••abcd");
        _apiKeys.Verify(s => s.ListAsync(
            "current-user",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_MapsExactConductorRequestAndNeverReturnsRawValue()
    {
        CreateApiKeyCommand? captured = null;
        var id = Guid.NewGuid();
        _apiKeys
            .Setup(s => s.CreateAsync(
                "current-user",
                It.IsAny<CreateApiKeyCommand>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, CreateApiKeyCommand, CancellationToken>(
                (_, command, _) => captured = command)
            .ReturnsAsync(Registration(
                id,
                "current-user",
                "openai-compatible",
                "sk-••••••••abcd",
                "model-x",
                "https://models.example/v1"));

        var result = await _controller.Create(
            new CreateApiKeyRequest(
                "Primary",
                "openai-compatible",
                "sk-raw-must-not-return-abcd",
                "model-x",
                "https://models.example/v1"),
            CancellationToken.None);

        var created = result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(201);
        var dto = created.Value.Should().BeOfType<ApiKeyEntry>().Subject;
        dto.Id.Should().Be(id);
        dto.MaskedValue.Should().Be("sk-••••••••abcd");
        typeof(ApiKeyEntry).GetProperties().Select(p => p.Name)
            .Should().NotContain("Value");
        captured.Should().Be(new CreateApiKeyCommand(
            "Primary",
            "openai-compatible",
            "sk-raw-must-not-return-abcd",
            "model-x",
            "https://models.example/v1"));
    }

    [Fact]
    public async Task Validate_ReturnsConductorValidationShape()
    {
        var id = Guid.NewGuid();
        _apiKeys
            .Setup(s => s.ValidateAsync(
                id,
                "current-user",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationOutcome(
                true,
                "Key is functional.",
                12));

        var result = await _controller.Validate(id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .Be(new ApiKeyValidationResult(true, "Key is functional.", 12));
    }

    [Fact]
    public async Task Create_WhenSettingsUnavailable_Returns503WithoutRawValue()
    {
        _apiKeys
            .Setup(s => s.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<CreateApiKeyCommand>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiKeySecretStoreUnavailableException(
                "The API-key secret store is unavailable."));

        var result = await _controller.Create(
            new CreateApiKeyRequest(
                "Primary",
                "openai",
                "sk-MUST-NOT-LEAK"),
            CancellationToken.None);

        var unavailable = result.Should().BeOfType<ObjectResult>().Subject;
        unavailable.StatusCode.Should().Be(503);
        System.Text.Json.JsonSerializer.Serialize(unavailable.Value)
            .Should().NotContain("sk-MUST-NOT-LEAK");
    }

    [Theory]
    [InlineData(nameof(ApiKeysController.List), "GET", null)]
    [InlineData(nameof(ApiKeysController.Create), "POST", null)]
    [InlineData(nameof(ApiKeysController.Update), "PUT", "{id:guid}")]
    [InlineData(nameof(ApiKeysController.Delete), "DELETE", "{id:guid}")]
    [InlineData(nameof(ApiKeysController.Validate), "POST", "{id:guid}/validate")]
    [InlineData(nameof(ApiKeysController.History), "GET", "{id:guid}/history")]
    public void Routes_MatchConductorClient(
        string methodName,
        string httpMethod,
        string? template)
    {
        var method = typeof(ApiKeysController).GetMethod(methodName)!;
        var attribute = method.GetCustomAttributes<HttpMethodAttribute>().Single();

        attribute.HttpMethods.Should().ContainSingle().Which.Should().Be(httpMethod);
        attribute.Template.Should().Be(template);
        typeof(ApiKeysController).GetCustomAttribute<RouteAttribute>()!
            .Template.Should().Be("api/apikeys");
    }

    private static ApiKeyRegistration Registration(
        Guid id,
        string owner,
        string provider,
        string masked,
        string? model = null,
        string? baseUrl = null)
        => new()
        {
            Id = id,
            OwnerId = owner,
            Name = "Primary",
            Provider = provider,
            SecretDefinitionKey =
                $"andy.models.providers.{provider}.apiKey",
            MaskedValue = masked,
            Model = model,
            BaseUrl = baseUrl,
        };
}

// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Verifies the classified GET
/// <c>/api/containers/{id}</c> response codes:
/// <list type="bullet">
///   <item>200 + X-Correlation-Id header on success.</item>
///   <item>404 structured envelope (code + correlationId) on confirmed deletion.</item>
///   <item>503 + Retry-After header on transient runtime unavailability.</item>
/// </list>
/// </summary>
public class ContainersControllerStatusClassificationTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService = new();
    private readonly Mock<ICurrentUserService> _mockCurrentUser = new();
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;

    public ContainersControllerStatusClassificationTests()
    {
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);

        _db = InMemoryDbHelper.CreateContext();
        var orgMembership = new Mock<IOrganizationMembershipService>();

        var httpContext = new DefaultHttpContext();
        _controller = new ContainersController(
            _mockService.Object,
            _mockCurrentUser.Object,
            _db,
            new Mock<IGitCloneService>().Object,
            new Mock<IGitCredentialService>().Object,
            new Mock<IGitRepositoryProbeService>().Object,
            orgMembership.Object,
            new Mock<IGitDiffService>().Object,
            new Mock<IPortDiscoveryService>().Object,
            new Mock<IContainerLifecycleBus>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            }
        };
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------
    // 200 OK — success path with correlation id header
    // ---------------------------------------------------------------

    [Fact]
    public async Task Get_ExistingContainer_Returns200_WithCorrelationIdHeader()
    {
        var containerId = Guid.NewGuid();
        var storyId = Guid.NewGuid();
        var container = new Container
        {
            Id = containerId,
            Name = "test",
            OwnerId = "test-user",
            Status = ContainerStatus.Running,
            StoryId = storyId,
        };
        _mockService.Setup(s => s.GetContainerAsync(containerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.Get(containerId, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.StatusCode.Should().Be(200);

        _controller.Response.Headers.Should().ContainKey("X-Correlation-Id");
        _controller.Response.Headers["X-Correlation-Id"].ToString().Should().Be(storyId.ToString());
    }

    [Fact]
    public async Task Get_ContainerWithoutStoryId_UsesContainerIdAsCorrelation()
    {
        var containerId = Guid.NewGuid();
        var container = new Container
        {
            Id = containerId,
            Name = "test",
            OwnerId = "test-user",
            Status = ContainerStatus.Running,
            StoryId = null,
        };
        _mockService.Setup(s => s.GetContainerAsync(containerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.Get(containerId, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _controller.Response.Headers["X-Correlation-Id"].ToString().Should().Be(containerId.ToString());
    }

    // ---------------------------------------------------------------
    // 404 — confirmed deletion (sustained)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Get_NonExistentContainer_Returns404_WithStructuredEnvelope()
    {
        var containerId = Guid.NewGuid();
        _mockService.Setup(s => s.GetContainerAsync(containerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.Get(containerId, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);

        // Envelope must carry the error code and correlationId.
        var value = notFound.Value;
        value.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        json.Should().Contain(ContainerNotFoundException.ErrorCode);
        json.Should().Contain(containerId.ToString());

        _controller.Response.Headers.Should().ContainKey("X-Correlation-Id");
    }

    // ---------------------------------------------------------------
    // 503 — transient runtime unavailability
    // ---------------------------------------------------------------

    [Fact]
    public async Task Get_RuntimeUnavailable_Returns503_WithRetryAfterHeader()
    {
        var containerId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        _mockService.Setup(s => s.GetContainerAsync(containerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContainerRuntimeUnavailableException(
                containerId, correlationId, "Docker daemon not responding", retryAfterSeconds: 30));

        var result = await _controller.Get(containerId, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(503);

        _controller.Response.Headers.Should().ContainKey("Retry-After");
        _controller.Response.Headers["Retry-After"].ToString().Should().Be("30");
        _controller.Response.Headers["X-Correlation-Id"].ToString().Should().Be(correlationId.ToString());

        var json = System.Text.Json.JsonSerializer.Serialize(statusResult.Value);
        json.Should().Contain(ContainerRuntimeUnavailableException.ErrorCode);
        json.Should().Contain(correlationId.ToString());
    }

    [Fact]
    public async Task Get_RuntimeUnavailable_503_IsDistinguishableFrom_404()
    {
        // This test proves the §7.2 invariant: a 503 MUST NOT be confused
        // with a 404 by Conductor's SM.0.4 helper. They must differ in
        // status code alone — body parsing is optional on the client side.
        var id = Guid.NewGuid();
        var corr = Guid.NewGuid();

        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContainerRuntimeUnavailableException(id, corr, "transient", 30));

        var transientResult = await _controller.Get(id, CancellationToken.None);
        var transientStatus = transientResult.Should().BeOfType<ObjectResult>().Subject.StatusCode;

        // Reset mock to throw KeyNotFoundException (404 path).
        _mockService.Setup(s => s.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var deletedResult = await _controller.Get(id, CancellationToken.None);
        var deletedStatus = deletedResult.Should().BeOfType<NotFoundObjectResult>().Subject.StatusCode;

        transientStatus.Should().Be(503);
        deletedStatus.Should().Be(404);
        transientStatus.Should().NotBe(deletedStatus);
    }
}

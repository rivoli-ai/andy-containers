// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// rivoli-ai/conductor#1972. An unknown container id on any
/// <c>/api/containers/{id}/…</c> sub-resource endpoint must surface the
/// same structured 404 envelope as <c>GET /api/containers/{id}</c>
/// (SM.2.6: <c>{ code, message, correlationId }</c> + the
/// <c>X-Correlation-Id</c> header) — never a 500.
///
/// Observed live: <c>GET /api/containers/{id}/git/diff</c> with id
/// <c>c5705ed9-29c2-4a13-ba89-d5080ddb546e</c> returned 500 with
/// <c>KeyNotFoundException: Container … not found</c>, which Conductor
/// rendered as <c>[PC-TASKLIVE-001] … [API-SERVER-500]</c>.
/// </summary>
public class ContainersControllerUnknownContainerNotFoundTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService = new();
    private readonly Mock<ICurrentUserService> _mockCurrentUser = new();
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;
    private readonly Guid _unknownId = Guid.NewGuid();

    public ContainersControllerUnknownContainerNotFoundTests()
    {
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);

        // The orchestration store's contract for an unknown id
        // (ContainerOrchestrationService.GetContainerAsync).
        _mockService.Setup(s => s.GetContainerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException($"Container {_unknownId} not found"));

        _db = InMemoryDbHelper.CreateContext();
        _controller = new ContainersController(
            _mockService.Object,
            _mockCurrentUser.Object,
            _db,
            new Mock<IGitCloneService>().Object,
            new Mock<IGitCredentialService>().Object,
            new Mock<IGitRepositoryProbeService>().Object,
            new Mock<IOrganizationMembershipService>().Object,
            new Mock<IGitDiffService>().Object,
            new Mock<IPortDiscoveryService>().Object,
            new Mock<IContainerLifecycleBus>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    public void Dispose() => _db.Dispose();

    private void AssertNotFoundEnvelope(IActionResult result)
    {
        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);

        var json = System.Text.Json.JsonSerializer.Serialize(notFound.Value);
        json.Should().Contain(ContainerNotFoundException.ErrorCode);
        json.Should().Contain(_unknownId.ToString());

        _controller.Response.Headers.Should().ContainKey("X-Correlation-Id");
        _controller.Response.Headers["X-Correlation-Id"].ToString()
            .Should().Be(_unknownId.ToString());
    }

    // ---- The two endpoints observed broken live (F6.1 / F6.4) ----

    [Fact]
    public async Task GetGitDiff_UnknownContainer_Returns404Envelope_Not500()
    {
        var result = await _controller.GetGitDiff(_unknownId, null, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task GetPorts_UnknownContainer_Returns404Envelope_Not500()
    {
        var result = await _controller.GetPorts(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    // ---- Every other sub-resource endpoint sharing the same lookup ----

    [Fact]
    public async Task Start_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.Start(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task Stop_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.Stop(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task Destroy_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.Destroy(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task Exec_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.Exec(
            _unknownId, new ExecRequest { Command = "echo hi" }, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task RetryCodeAssistantInstall_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.RetryCodeAssistantInstall(
            _unknownId, new Mock<ICodeAssistantInstallExecutor>().Object, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task GetConnectionInfo_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.GetConnectionInfo(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task GetEvents_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.GetEvents(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task ListRepositories_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.ListRepositories(_unknownId, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task AddRepository_UnknownContainer_Returns404Envelope()
    {
        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git" };
        var result = await _controller.AddRepository(_unknownId, dto, CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }

    [Fact]
    public async Task PullRepository_UnknownContainer_Returns404Envelope()
    {
        var result = await _controller.PullRepository(
            _unknownId, Guid.NewGuid(), CancellationToken.None);
        AssertNotFoundEnvelope(result);
    }
}

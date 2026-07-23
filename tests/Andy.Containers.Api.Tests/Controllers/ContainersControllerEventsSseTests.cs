// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
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
/// Security regression coverage for #318. The lifecycle bus is fleet-wide,
/// but the HTTP stream must filter both replay and live events to containers
/// visible to the current principal.
/// </summary>
public sealed class ContainersControllerEventsSseTests : IDisposable
{
    private readonly ContainersDbContext _db = InMemoryDbHelper.CreateContext();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IOrganizationMembershipService> _orgMembership = new();

    public ContainersControllerEventsSseTests()
    {
        _currentUser.Setup(u => u.GetUserId()).Returns("viewer");
        _currentUser.Setup(u => u.GetEmail()).Returns((string?)null);
        _currentUser.Setup(u => u.IsAdmin()).Returns(false);
        _currentUser.Setup(u => u.IsServiceAccount()).Returns(false);
        _orgMembership
            .Setup(o => o.IsMemberAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Events_NonAdmin_EmitsOnlyOwnedContainer()
    {
        var owned = SeedContainer("viewer");
        var foreign = SeedContainer("another-user");
        var controller = CreateController(
            Envelope(1, foreign, "running"),
            Envelope(2, owned, "running"));
        var bodyStream = SetupResponse(controller);

        await controller.Events(CancellationToken.None);

        var body = ReadBody(bodyStream);
        body.Should().Contain($"\"containerId\":\"{owned.Id}\"");
        body.Should().NotContain(foreign.Id.ToString());
        body.Should().Contain("id: 2\n",
            "global sequence ids remain stable even when an earlier event is filtered");
    }

    [Fact]
    public async Task Events_SameOrganizationMember_CanReadLifecycle()
    {
        var organizationId = Guid.NewGuid();
        var container = SeedContainer("another-user", organizationId);
        _orgMembership
            .Setup(o => o.IsMemberAsync(
                "viewer", organizationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = CreateController(Envelope(1, container, "creating"));
        var bodyStream = SetupResponse(controller);

        await controller.Events(CancellationToken.None);

        ReadBody(bodyStream).Should().Contain(container.Id.ToString());
    }

    [Fact]
    public async Task Events_Admin_EmitsAllContainers()
    {
        _currentUser.Setup(u => u.IsAdmin()).Returns(true);
        var first = SeedContainer("first-user");
        var second = SeedContainer("second-user");
        var controller = CreateController(
            Envelope(1, first, "creating"),
            Envelope(2, second, "running"));
        var bodyStream = SetupResponse(controller);

        await controller.Events(CancellationToken.None);

        var body = ReadBody(bodyStream);
        body.Should().Contain(first.Id.ToString());
        body.Should().Contain(second.Id.ToString());
    }

    [Fact]
    public async Task Events_UnknownContainerId_FailsClosed()
    {
        var unknown = Guid.NewGuid();
        var controller = CreateController(new ContainerLifecycleEnvelope(
            1,
            new ContainerLifecycleEvent(
                unknown,
                "running",
                new ContainerLifecyclePhaseData(),
                unknown,
                DateTimeOffset.UtcNow)));
        var bodyStream = SetupResponse(controller);

        await controller.Events(CancellationToken.None);

        ReadBody(bodyStream).Should().BeEmpty();
    }

    private ContainersController CreateController(params ContainerLifecycleEnvelope[] events)
        => new(
            Mock.Of<IContainerService>(),
            _currentUser.Object,
            _db,
            Mock.Of<IGitCloneService>(),
            Mock.Of<IGitCredentialService>(),
            Mock.Of<IGitRepositoryProbeService>(),
            _orgMembership.Object,
            Mock.Of<IGitDiffService>(),
            Mock.Of<IPortDiscoveryService>(),
            new FiniteLifecycleBus(events),
            Mock.Of<IRunOutputBus>());

    private static MemoryStream SetupResponse(ContainersController controller)
    {
        var stream = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = stream;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return stream;
    }

    private static string ReadBody(MemoryStream stream)
    {
        stream.Position = 0;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private Container SeedContainer(string ownerId, Guid? organizationId = null)
    {
        var template = new ContainerTemplate
        {
            Code = $"events-{Guid.NewGuid():N}",
            Name = "Events test",
            Version = "1",
            BaseImage = "ubuntu:24.04",
        };
        var provider = new InfrastructureProvider
        {
            Code = $"events-{Guid.NewGuid():N}",
            Name = "Events test",
            Type = ProviderType.Docker,
            IsEnabled = true,
        };
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = $"events-{Guid.NewGuid():N}",
            OwnerId = ownerId,
            OrganizationId = organizationId,
            Status = ContainerStatus.Running,
            Template = template,
            Provider = provider,
        };
        _db.Containers.Add(container);
        _db.SaveChanges();
        return container;
    }

    private static ContainerLifecycleEnvelope Envelope(
        long sequence,
        Container container,
        string phase)
        => new(
            sequence,
            new ContainerLifecycleEvent(
                container.Id,
                phase,
                new ContainerLifecyclePhaseData(),
                container.Id,
                DateTimeOffset.UtcNow));

    private sealed class FiniteLifecycleBus(
        IReadOnlyList<ContainerLifecycleEnvelope> events) : IContainerLifecycleBus
    {
        public void Publish(ContainerLifecycleEvent @event)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ContainerLifecycleEnvelope> SubscribeAsync(
            long? lastEventId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var envelope in events)
            {
                ct.ThrowIfCancellationRequested();
                if (lastEventId is null || envelope.SequenceNumber > lastEventId.Value)
                {
                    yield return envelope;
                }
            }
            await Task.CompletedTask;
        }
    }
}

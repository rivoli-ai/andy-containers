using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Grpc;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Grpc.Core;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerGrpcServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ISshKeyService> _mockSshKeyService;
    private readonly Mock<IContainerService> _mockContainerService;
    private readonly ContainerGrpcService _service;

    public ContainerGrpcServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockSshKeyService = new Mock<ISshKeyService>();
        _mockContainerService = new Mock<IContainerService>();
        _service = new ContainerGrpcService(_db, _mockSshKeyService.Object, _mockContainerService.Object);
    }

    public void Dispose() => _db.Dispose();

    private static ServerCallContext MockContext() => new MockServerCallContext();

    [Fact]
    public async Task ListUserSshKeys_ReturnsKeysForUser()
    {
        var keys = new List<UserSshKey>
        {
            new() { UserId = "user1", Label = "Laptop", PublicKey = "ssh-ed25519 AAAA", Fingerprint = "SHA256:abc", KeyType = "ed25519" },
            new() { UserId = "user1", Label = "CI", PublicKey = "ssh-rsa AAAA", Fingerprint = "SHA256:def", KeyType = "rsa" }
        };
        _mockSshKeyService.Setup(s => s.ListKeysAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);

        var response = await _service.ListUserSshKeys(
            new ListUserSshKeysRequest { UserId = "user1" }, MockContext());

        response.Keys.Should().HaveCount(2);
        response.Keys[0].Label.Should().Be("Laptop");
        response.Keys[0].KeyType.Should().Be("ed25519");
    }

    [Fact]
    public async Task AddSshKey_ValidKey_ReturnsKeyResponse()
    {
        var key = new UserSshKey
        {
            UserId = "grpc-user", Label = "gRPC Key", PublicKey = "ssh-ed25519 AAAA",
            Fingerprint = "SHA256:grpcfp", KeyType = "ed25519"
        };
        _mockSshKeyService.Setup(s => s.IsValidPublicKey("ssh-ed25519 AAAA")).Returns(true);
        _mockSshKeyService.Setup(s => s.AddKeyAsync("grpc-user", "gRPC Key", "ssh-ed25519 AAAA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(key);

        var response = await _service.AddSshKey(
            new AddSshKeyRequest { Label = "gRPC Key", PublicKey = "ssh-ed25519 AAAA" }, MockContext());

        response.Label.Should().Be("gRPC Key");
        response.Fingerprint.Should().Be("SHA256:grpcfp");
    }

    [Fact]
    public async Task AddSshKey_InvalidKey_ThrowsInvalidArgument()
    {
        _mockSshKeyService.Setup(s => s.IsValidPublicKey("bad-key")).Returns(false);

        var act = () => _service.AddSshKey(
            new AddSshKeyRequest { Label = "Bad", PublicKey = "bad-key" }, MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RemoveSshKey_Existing_ReturnsSuccess()
    {
        var keyId = Guid.NewGuid();
        _mockSshKeyService.Setup(s => s.RemoveKeyAsync("grpc-user", keyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var response = await _service.RemoveSshKey(
            new RemoveSshKeyRequest { KeyId = keyId.ToString() }, MockContext());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSshKey_NonExistent_ThrowsNotFound()
    {
        var keyId = Guid.NewGuid();
        _mockSshKeyService.Setup(s => s.RemoveKeyAsync("grpc-user", keyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => _service.RemoveSshKey(
            new RemoveSshKeyRequest { KeyId = keyId.ToString() }, MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task GetContainerSshConfig_SshEnabled_ReturnsConfig()
    {
        var container = new Container { Name = "grpc-ssh", OwnerId = "user1", SshEnabled = true };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var response = await _service.GetContainerSshConfig(
            new ContainerIdRequest { ContainerId = container.Id.ToString() }, MockContext());

        response.SshEnabled.Should().BeTrue();
        response.Username.Should().Be("dev");
        response.ConfigSnippet.Should().Contain("Host andy-container-");
    }

    [Fact]
    public async Task GetContainerSshConfig_NonExistent_ThrowsNotFound()
    {
        var act = () => _service.GetContainerSshConfig(
            new ContainerIdRequest { ContainerId = Guid.NewGuid().ToString() }, MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task GetContainerSshConfig_SshNotEnabled_ThrowsFailedPrecondition()
    {
        var container = new Container { Name = "no-ssh", OwnerId = "user1", SshEnabled = false };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var act = () => _service.GetContainerSshConfig(
            new ContainerIdRequest { ContainerId = container.Id.ToString() }, MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.FailedPrecondition);
    }

    // === Story 12: GetConnectionInfo gRPC ===

    [Fact]
    public async Task GetConnectionInfo_SshEnabled_ReturnsSshUser()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.GetConnectionInfoAsync(containerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Abstractions.ConnectionInfo
            {
                SshEndpoint = "localhost:2222",
                SshUser = "dev",
                IdeEndpoint = "https://ide.test"
            });

        var response = await _service.GetConnectionInfo(
            new ContainerIdRequest { ContainerId = containerId.ToString() }, MockContext());

        response.SshEndpoint.Should().Be("localhost:2222");
        response.SshUser.Should().Be("dev");
        response.IdeEndpoint.Should().Be("https://ide.test");
    }

    [Fact]
    public async Task GetConnectionInfo_SshNotEnabled_ReturnsEmptySshUser()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.GetConnectionInfoAsync(containerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Abstractions.ConnectionInfo
            {
                IdeEndpoint = "https://ide.test"
            });

        var response = await _service.GetConnectionInfo(
            new ContainerIdRequest { ContainerId = containerId.ToString() }, MockContext());

        response.SshEndpoint.Should().BeEmpty();
        response.SshUser.Should().BeEmpty();
    }
}

internal class MockServerCallContext : ServerCallContext
{
    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "test-peer";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => [];
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new("test", new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotImplementedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
        Task.CompletedTask;
}

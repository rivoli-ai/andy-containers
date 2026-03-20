using Andy.Containers.Abstractions;
using Andy.Containers.Grpc;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ContainerGrpcService : ContainerService.ContainerServiceBase
{
    private readonly ContainersDbContext _db;
    private readonly ISshKeyService _sshKeyService;
    private readonly IContainerService _containerService;

    public ContainerGrpcService(ContainersDbContext db, ISshKeyService sshKeyService, IContainerService containerService)
    {
        _db = db;
        _sshKeyService = sshKeyService;
        _containerService = containerService;
    }

    public override async Task<ListUserSshKeysResponse> ListUserSshKeys(ListUserSshKeysRequest request, ServerCallContext context)
    {
        var userId = request.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id is required"));

        var keys = await _sshKeyService.ListKeysAsync(userId, context.CancellationToken);
        var response = new ListUserSshKeysResponse();
        foreach (var key in keys)
        {
            response.Keys.Add(MapSshKey(key));
        }
        return response;
    }

    public override async Task<SshKeyResponse> AddSshKey(AddSshKeyRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PublicKey))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "public_key is required"));

        if (!_sshKeyService.IsValidPublicKey(request.PublicKey))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid SSH public key format"));

        try
        {
            // Use a placeholder user ID — in production this comes from auth metadata
            var userId = "grpc-user";
            var key = await _sshKeyService.AddKeyAsync(userId, request.Label, request.PublicKey, context.CancellationToken);
            return MapSshKey(key);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<RemoveSshKeyResponse> RemoveSshKey(RemoveSshKeyRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.KeyId, out var keyId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid key_id format"));

        // Use a placeholder user ID — in production this comes from auth metadata
        var userId = "grpc-user";
        var removed = await _sshKeyService.RemoveKeyAsync(userId, keyId, context.CancellationToken);
        if (!removed)
            throw new RpcException(new Status(StatusCode.NotFound, "SSH key not found"));

        return new RemoveSshKeyResponse { Success = true };
    }

    public override async Task<SshConfigResponse> GetContainerSshConfig(ContainerIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ContainerId, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid container_id format"));

        var container = await _db.Containers.FindAsync([id], context.CancellationToken);
        if (container is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Container not found"));

        if (!container.SshEnabled)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "SSH is not enabled on this container"));

        var shortId = container.Id.ToString()[..8];
        var configSnippet = $"""
            Host andy-container-{shortId}
              HostName localhost
              Port 22
              User dev
              StrictHostKeyChecking no
              UserKnownHostsFile /dev/null
            """;

        return new SshConfigResponse
        {
            SshEnabled = true,
            Host = "localhost",
            Port = 22,
            Username = "dev",
            ConfigSnippet = configSnippet
        };
    }

    public override async Task<ConnectionInfoResponse> GetConnectionInfo(ContainerIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ContainerId, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid container_id format"));

        try
        {
            var info = await _containerService.GetConnectionInfoAsync(id, context.CancellationToken);
            return new ConnectionInfoResponse
            {
                IpAddress = info.IpAddress ?? "",
                IdeEndpoint = info.IdeEndpoint ?? "",
                VncEndpoint = info.VncEndpoint ?? "",
                SshEndpoint = info.SshEndpoint ?? "",
                SshUser = info.SshUser ?? "",
                AgentEndpoint = info.AgentEndpoint ?? ""
            };
        }
        catch (KeyNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Container not found"));
        }
    }

    private static SshKeyResponse MapSshKey(UserSshKey key) => new()
    {
        Id = key.Id.ToString(),
        UserId = key.UserId,
        Label = key.Label,
        Fingerprint = key.Fingerprint,
        KeyType = key.KeyType,
        CreatedAt = key.CreatedAt.ToString("O"),
        LastUsedAt = key.LastUsedAt?.ToString("O") ?? ""
    };
}

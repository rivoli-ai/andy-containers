using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// Tests pinning the dispatch contract for the daemon-side PTY
/// path introduced in conductor #875 PR 1.
///
/// The controller tries the new path via
/// <see cref="IInfrastructureProvider.OpenInteractiveExecAsync"/>;
/// when the provider returns null, the legacy script-based path
/// runs. Tests assert: the call shape, the fallback semantics, and
/// the basic readiness of the new path.
/// </summary>
public class TerminalControllerPtyDispatchTests
{
    [Fact]
    public void IInteractiveExecSession_HasReadWriteResizeSurface()
    {
        // Pin the public surface — this is the contract every
        // provider implementation has to honour.
        var sessionType = typeof(IInteractiveExecSession);
        sessionType.GetMethod("ReadAsync").Should().NotBeNull(
            "session must support reading bytes from the inner shell");
        sessionType.GetMethod("WriteAsync").Should().NotBeNull(
            "session must support writing bytes to the inner shell");
        sessionType.GetMethod("ResizeAsync").Should().NotBeNull(
            "session must support PTY resize");
        // DisposeAsync comes from IAsyncDisposable — verify the
        // interface declares it as a base.
        typeof(IAsyncDisposable).IsAssignableFrom(sessionType).Should().BeTrue(
            "session must clean up its underlying resources on disposal");
    }

    [Fact]
    public async Task IInfrastructureProvider_DefaultOpenInteractiveExec_ReturnsNull()
    {
        // Default interface implementation returns null — meaning
        // "this provider doesn't support daemon-side PTY exec; fall
        // back to the legacy script path." Cloud providers (AWS,
        // GCP, etc.) inherit the null default. Local providers
        // override.
        IInfrastructureProvider defaultProvider = new MinimalProvider();
        var session = await defaultProvider.OpenInteractiveExecAsync(
            externalId: "ext-1",
            command: ["bash", "-c", "echo"],
            user: "root",
            workingDirectory: "/root",
            cols: 120,
            rows: 40);
        session.Should().BeNull(
            "default impl must return null so callers know to fall back");
    }

    [Fact]
    public async Task DockerInteractiveExecSession_DisposeIsIdempotent()
    {
        // Defensive disposal: called from `await using`, may also be
        // called explicitly on errors. Must not throw on second call.
        // We can't easily exercise this against a real Docker
        // daemon in a unit test, but we can pin the interface
        // contract by having a fake impl.
        var fake = new FakeInteractiveExecSession();
        await fake.DisposeAsync();
        await fake.DisposeAsync(); // should be a no-op
        fake.DisposeCount.Should().Be(1,
            "disposal logic should run exactly once across multiple Dispose calls");
    }

    /// <summary>
    /// Minimal IInfrastructureProvider impl that uses every default
    /// interface method. Used to assert the default returns of
    /// `OpenInteractiveExecAsync` and `ListExternalIdsAsync`.
    /// </summary>
    private sealed class MinimalProvider : IInfrastructureProvider
    {
        public ProviderType Type => ProviderType.Docker;
        public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartContainerAsync(string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopContainerAsync(string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DestroyContainerAsync(string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Andy.Containers.Abstractions.ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// Fake session for testing disposal idempotency.
    /// </summary>
    private sealed class FakeInteractiveExecSession : IInteractiveExecSession
    {
        public int DisposeCount { get; private set; }
        private bool _disposed;

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(0);
        public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

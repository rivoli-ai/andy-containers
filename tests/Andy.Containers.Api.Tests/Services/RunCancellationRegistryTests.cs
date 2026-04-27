using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// AP7 (rivoli-ai/andy-containers#109). The registry is the only piece of
// shared state between the cancel endpoint and the headless runner; if
// these invariants drift, cancels silently no-op or hang the request
// thread. Each test pins one observable behaviour rather than the
// implementation, so the registry can be re-shaped (e.g. swapped for a
// distributed control channel) without rewriting the suite.
public class RunCancellationRegistryTests
{
    [Fact]
    public void Register_LinkedTokenObservesOuterCancellation()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var outer = new CancellationTokenSource();

        using var registration = registry.Register(runId, outer.Token);

        registration.Token.IsCancellationRequested.Should().BeFalse();
        outer.Cancel();
        registration.Token.IsCancellationRequested.Should().BeTrue(
            "the runner's exec token is linked to the caller's outer ct");
    }

    [Fact]
    public void TryCancel_SignalsLinkedToken_AndReturnsTrue()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var registration = registry.Register(runId, CancellationToken.None);

        var cancelled = registry.TryCancel(runId);

        cancelled.Should().BeTrue();
        registration.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void TryCancel_UnknownRun_ReturnsFalse()
    {
        var registry = new RunCancellationRegistry();

        registry.TryCancel(Guid.NewGuid()).Should().BeFalse(
            "the cancel endpoint relies on this signal to fall back to the no-runner path");
    }

    [Fact]
    public void Register_DuplicateRunId_Throws()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var first = registry.Register(runId, CancellationToken.None);

        var act = () => registry.Register(runId, CancellationToken.None);

        act.Should().Throw<InvalidOperationException>(
            "AP6 starts at most one runner per Run; double-register is a caller bug");
    }

    [Fact]
    public void Register_AfterDispose_AllowsReregistration()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        var first = registry.Register(runId, CancellationToken.None);
        first.Dispose();

        var act = () => registry.Register(runId, CancellationToken.None);

        act.Should().NotThrow(
            "after a runner exits the slot must free up for the next attempt");
    }

    [Fact]
    public async Task WaitForTerminalAsync_UnregisteredRun_ReturnsTrueImmediately()
    {
        var registry = new RunCancellationRegistry();

        var result = await registry.WaitForTerminalAsync(
            Guid.NewGuid(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeTrue(
            "no registration ⇒ already terminal or never started; refetch the row");
    }

    [Fact]
    public async Task WaitForTerminalAsync_DisposalSignalsWaiter()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        var registration = registry.Register(runId, CancellationToken.None);

        var waitTask = registry.WaitForTerminalAsync(
            runId, TimeSpan.FromSeconds(5), CancellationToken.None);

        registration.Dispose();

        var result = await waitTask;
        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForTerminalAsync_TimeoutReturnsFalse()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var registration = registry.Register(runId, CancellationToken.None);

        var result = await registry.WaitForTerminalAsync(
            runId, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        result.Should().BeFalse("the runner never disposed within the grace");
    }

    [Fact]
    public async Task WaitForTerminalAsync_CallerCancellation_ThrowsOperationCanceled()
    {
        var registry = new RunCancellationRegistry();
        var runId = Guid.NewGuid();
        using var registration = registry.Register(runId, CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waitTask = registry.WaitForTerminalAsync(runId, TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => waitTask)
            .Should().ThrowAsync<OperationCanceledException>(
                "caller hangup must propagate, not be swallowed as a timeout");
    }
}

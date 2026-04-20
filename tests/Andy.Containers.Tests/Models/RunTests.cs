using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class RunTests
{
    [Fact]
    public void NewRun_HasPendingStatus_ByDefault()
    {
        var run = new Run { AgentId = "triage-agent" };

        run.Status.Should().Be(RunStatus.Pending);
    }

    [Fact]
    public void NewRun_SetsCreatedAndUpdatedAt()
    {
        var before = DateTimeOffset.UtcNow;

        var run = new Run { AgentId = "triage-agent" };

        run.CreatedAt.Should().BeOnOrAfter(before);
        run.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void TransitionTo_FollowsHappyPath_Pending_Provisioning_Running_Succeeded()
    {
        var run = new Run { AgentId = "triage-agent" };

        run.TransitionTo(RunStatus.Provisioning);
        run.Status.Should().Be(RunStatus.Provisioning);

        run.TransitionTo(RunStatus.Running);
        run.Status.Should().Be(RunStatus.Running);
        run.StartedAt.Should().NotBeNull("entering Running stamps StartedAt");

        run.TransitionTo(RunStatus.Succeeded);
        run.Status.Should().Be(RunStatus.Succeeded);
        run.EndedAt.Should().NotBeNull("terminal state stamps EndedAt");
    }

    [Theory]
    [InlineData(RunStatus.Pending, RunStatus.Provisioning)]
    [InlineData(RunStatus.Pending, RunStatus.Cancelled)]
    [InlineData(RunStatus.Pending, RunStatus.Failed)]
    [InlineData(RunStatus.Provisioning, RunStatus.Running)]
    [InlineData(RunStatus.Provisioning, RunStatus.Cancelled)]
    [InlineData(RunStatus.Provisioning, RunStatus.Failed)]
    [InlineData(RunStatus.Provisioning, RunStatus.Timeout)]
    [InlineData(RunStatus.Running, RunStatus.Succeeded)]
    [InlineData(RunStatus.Running, RunStatus.Failed)]
    [InlineData(RunStatus.Running, RunStatus.Cancelled)]
    [InlineData(RunStatus.Running, RunStatus.Timeout)]
    public void CanTransition_AllowsLegalEdges(RunStatus from, RunStatus to)
    {
        RunStatusTransitions.CanTransition(from, to).Should().BeTrue(
            $"{from} → {to} is part of the documented state machine");
    }

    [Theory]
    // Non-terminal reverse edges
    [InlineData(RunStatus.Running, RunStatus.Pending)]
    [InlineData(RunStatus.Running, RunStatus.Provisioning)]
    [InlineData(RunStatus.Provisioning, RunStatus.Pending)]
    // Skipping states
    [InlineData(RunStatus.Pending, RunStatus.Running)]
    [InlineData(RunStatus.Pending, RunStatus.Succeeded)]
    // Terminal → anything (terminal states are absorbing)
    [InlineData(RunStatus.Succeeded, RunStatus.Running)]
    [InlineData(RunStatus.Failed, RunStatus.Running)]
    [InlineData(RunStatus.Cancelled, RunStatus.Running)]
    [InlineData(RunStatus.Timeout, RunStatus.Running)]
    [InlineData(RunStatus.Succeeded, RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled, RunStatus.Succeeded)]
    public void CanTransition_RejectsIllegalEdges(RunStatus from, RunStatus to)
    {
        RunStatusTransitions.CanTransition(from, to).Should().BeFalse(
            $"{from} → {to} violates the state machine");
    }

    [Fact]
    public void TransitionTo_ThrowsOnIllegalEdge()
    {
        var run = new Run { AgentId = "triage-agent" }; // Pending
        var act = () => run.TransitionTo(RunStatus.Succeeded);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Pending -> Succeeded*");
    }

    [Fact]
    public void TransitionTo_ThrowsFromTerminalState()
    {
        var run = new Run { AgentId = "triage-agent" };
        run.TransitionTo(RunStatus.Provisioning);
        run.TransitionTo(RunStatus.Running);
        run.TransitionTo(RunStatus.Succeeded);

        var act = () => run.TransitionTo(RunStatus.Running);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void IsTerminal_IdentifiesTerminalStates()
    {
        RunStatusTransitions.IsTerminal(RunStatus.Pending).Should().BeFalse();
        RunStatusTransitions.IsTerminal(RunStatus.Provisioning).Should().BeFalse();
        RunStatusTransitions.IsTerminal(RunStatus.Running).Should().BeFalse();
        RunStatusTransitions.IsTerminal(RunStatus.Succeeded).Should().BeTrue();
        RunStatusTransitions.IsTerminal(RunStatus.Failed).Should().BeTrue();
        RunStatusTransitions.IsTerminal(RunStatus.Cancelled).Should().BeTrue();
        RunStatusTransitions.IsTerminal(RunStatus.Timeout).Should().BeTrue();
    }

    [Fact]
    public void TransitionTo_UsesExplicitTimestamp_WhenProvided()
    {
        var fixedAt = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
        var run = new Run { AgentId = "triage-agent" };

        run.TransitionTo(RunStatus.Provisioning, fixedAt);
        run.TransitionTo(RunStatus.Running, fixedAt);

        run.StartedAt.Should().Be(fixedAt);
        run.UpdatedAt.Should().Be(fixedAt);
    }

    [Fact]
    public void TransitionTo_DoesNotOverwriteExistingStartedAt()
    {
        var run = new Run { AgentId = "triage-agent" };
        run.TransitionTo(RunStatus.Provisioning);
        var firstRun = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
        run.TransitionTo(RunStatus.Running, firstRun);

        var storedStart = run.StartedAt;

        // A no-op re-entry into Running would violate the state machine anyway,
        // but the invariant is: once StartedAt is set, transitioning through
        // further states (e.g. terminal) must not reset it.
        run.TransitionTo(RunStatus.Succeeded, firstRun.AddSeconds(30));

        run.StartedAt.Should().Be(storedStart, "StartedAt is set once");
        run.EndedAt.Should().Be(firstRun.AddSeconds(30));
    }

    [Fact]
    public void WorkspaceRef_DefaultsToEmpty()
    {
        var run = new Run { AgentId = "triage-agent" };

        run.WorkspaceRef.Should().NotBeNull();
        run.WorkspaceRef.WorkspaceId.Should().Be(Guid.Empty);
        run.WorkspaceRef.Branch.Should().BeNull();
    }
}

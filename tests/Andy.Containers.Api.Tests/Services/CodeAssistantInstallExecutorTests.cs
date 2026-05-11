using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#945 (M1.5.3). Pins the executor's contract:
/// every outcome (Installed / Failed / Skipped) writes the matching
/// status + reason + timestamp on the Container row. The UI relies
/// on this — a missing status means a blank banner.
/// </summary>
public class CodeAssistantInstallExecutorTests
{
    private readonly Mock<ICodeAssistantInstallService> _installService = new();
    private readonly Mock<IContainerService> _containerService = new();

    private CodeAssistantInstallExecutor MakeExecutor(DateTimeOffset? now = null)
    {
        var time = now is null
            ? (TimeProvider)TimeProvider.System
            : new FakeTimeProvider(now.Value);
        return new CodeAssistantInstallExecutor(
            _installService.Object,
            _containerService.Object,
            NullLogger<CodeAssistantInstallExecutor>.Instance,
            time);
    }

    private static Container MakeContainer(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "test-ctr",
        OwnerId = "owner",
        TemplateId = Guid.NewGuid(),
        ProviderId = Guid.NewGuid(),
        Status = ContainerStatus.Running,
    };

    private static CodeAssistantConfig ClaudeConfig() => new()
    {
        Tool = CodeAssistantType.ClaudeCode,
        AutoStart = false,
    };

    // -----------------------------------------------------------------
    // Outcome: Installed
    // -----------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ExitCodeZero_WritesInstalledStatus()
    {
        _installService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("echo ok");
        _containerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "ok" });
        var fixedNow = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var executor = MakeExecutor(fixedNow);
        var container = MakeContainer();

        await executor.RunAsync(container, ClaudeConfig(), CancellationToken.None);

        container.CodeAssistantStatus.Should().Be(CodeAssistantInstallStatus.Installed);
        container.CodeAssistantStatusReason.Should().BeNull();
        container.CodeAssistantStatusAt.Should().Be(fixedNow.UtcDateTime);
    }

    // -----------------------------------------------------------------
    // Outcome: Failed
    // -----------------------------------------------------------------

    [Fact]
    public async Task RunAsync_NonZeroExitCode_WritesFailedStatusWithStderrSummary()
    {
        _installService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("echo fail; exit 7");
        _containerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 7, StdErr = "ENOENT: npm not found\nbash: line 4: command not found" });
        var executor = MakeExecutor();
        var container = MakeContainer();

        await executor.RunAsync(container, ClaudeConfig(), CancellationToken.None);

        container.CodeAssistantStatus.Should().Be(CodeAssistantInstallStatus.Failed);
        container.CodeAssistantStatusReason.Should().StartWith("exit-code-7:");
        container.CodeAssistantStatusReason.Should().Contain("ENOENT");
        container.CodeAssistantStatusReason.Should().Contain("|",
            "multi-line stderr should be flattened to a single line for the UI banner.");
        container.CodeAssistantStatusAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_ExecAsyncTimeoutOpcodecanceled_WritesFailedTimeoutStatus()
    {
        _installService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("sleep 9999");
        _containerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("install timed out"));
        var executor = MakeExecutor();
        var container = MakeContainer();

        // Caller's CT is NOT cancelled — the cancellation came from
        // the executor's internal timeout. That's the path the
        // executor must classify as a Failed timeout, not as a global
        // shutdown propagation.
        await executor.RunAsync(container, ClaudeConfig(), CancellationToken.None);

        container.CodeAssistantStatus.Should().Be(CodeAssistantInstallStatus.Failed);
        container.CodeAssistantStatusReason.Should().StartWith("timeout:");
    }

    [Fact]
    public async Task RunAsync_ExecAsyncThrowsArbitraryException_WritesFailedExceptionStatus()
    {
        _installService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("echo");
        _containerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker daemon unreachable"));
        var executor = MakeExecutor();
        var container = MakeContainer();

        await executor.RunAsync(container, ClaudeConfig(), CancellationToken.None);

        container.CodeAssistantStatus.Should().Be(CodeAssistantInstallStatus.Failed);
        container.CodeAssistantStatusReason.Should().StartWith("exception: InvalidOperationException:");
        container.CodeAssistantStatusReason.Should().Contain("docker daemon unreachable");
    }

    // -----------------------------------------------------------------
    // Outcome: Skipped (script generation failed)
    // -----------------------------------------------------------------

    [Fact]
    public async Task RunAsync_GenerateInstallScriptThrows_WritesSkippedStatus()
    {
        _installService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Throws(new NotSupportedException("Unknown tool: QwenCoder"));
        var executor = MakeExecutor();
        var container = MakeContainer();

        await executor.RunAsync(container, ClaudeConfig(), CancellationToken.None);

        container.CodeAssistantStatus.Should().Be(CodeAssistantInstallStatus.Skipped);
        container.CodeAssistantStatusReason.Should().StartWith("script-generation: NotSupportedException:");
        container.CodeAssistantStatusReason.Should().Contain("Unknown tool");
        // Important: no exec call when script generation fails.
        _containerService.Verify(s => s.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -----------------------------------------------------------------
    // Lifecycle: Installing flag is set before the script runs
    // -----------------------------------------------------------------

    [Fact]
    public async Task RunAsync_MarksInstallingBeforeScriptRuns()
    {
        // Capture the container's status mid-flight by inspecting it
        // when ExecAsync is invoked.
        CodeAssistantInstallStatus? statusDuringExec = null;
        _installService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("echo");
        var container = MakeContainer();
        _containerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => statusDuringExec = container.CodeAssistantStatus)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        var executor = MakeExecutor();

        await executor.RunAsync(container, ClaudeConfig(), CancellationToken.None);

        statusDuringExec.Should().Be(CodeAssistantInstallStatus.Installing,
            "UI should be able to render an 'install in progress' affordance even before the script finishes.");
    }

    // -----------------------------------------------------------------
    // SummariseReason
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(null, "<no detail>")]
    [InlineData("", "<no detail>")]
    [InlineData("   ", "<no detail>")]
    [InlineData("single line", "single line")]
    [InlineData("a\nb\nc", "a | b | c")]
    [InlineData("  a  \r\n  b  \r\n", "a | b")]
    public void SummariseReason_HandlesCommonInputs(string? input, string expected)
    {
        CodeAssistantInstallExecutor.SummariseReason(input).Should().Be(expected);
    }

    [Fact]
    public void SummariseReason_TruncatesLongInput()
    {
        var input = new string('x', CodeAssistantInstallExecutor.StatusReasonMaxLength + 50);
        var summary = CodeAssistantInstallExecutor.SummariseReason(input);
        summary.Length.Should().Be(CodeAssistantInstallExecutor.StatusReasonMaxLength);
        summary.Should().EndWith("…");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

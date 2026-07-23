using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// AX.16 (rivoli-ai/conductor#2104). The headless andy-cli exec is detached
// from the HTTP request: POST /api/runs returns as soon as the run is
// Provisioning, and the launcher drives the exec on a background task in a
// fresh DI scope. The headline regression here — Dispatch_ReturnsPromptly_
// WhileRunnerStillExecuting — HANGS on the old code, where the dispatcher
// awaited the runner inside the request and andy-tasks' 100 s HttpClient
// timeout failed every plan task whose agent ran longer than that.
public class HeadlessRunLauncherTests
{
    private const string ConfigPath = "/tmp/runs/x/config.json";

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private static Run SeedRun(ContainersDbContext db)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "coding",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Provisioning,
            ContainerId = Guid.NewGuid(),
            WorkspaceRef = new WorkspaceRef { WorkspaceId = Guid.NewGuid() },
        };
        db.Runs.Add(run);
        db.SaveChanges();
        return run;
    }

    [Fact]
    public async Task Dispatch_ReturnsPromptly_WhileRunnerStillExecuting()
    {
        // THE AX.16 regression test. Wire the REAL launcher into the REAL
        // dispatcher with a runner that does not complete until released.
        // On the old (synchronous) dispatcher this test never returns from
        // DispatchAsync; on the new one it returns Detached immediately.
        var dbName = Guid.NewGuid().ToString();
        var db = InMemoryDbHelper.CreateContext(dbName);

        var template = new ContainerTemplate
        {
            Code = "headless-launcher-test",
            Name = "Headless launcher test",
            Version = "1",
            BaseImage = "ubuntu:24.04",
        };
        var provider = new InfrastructureProvider
        {
            Code = "headless-launcher-test",
            Name = "Headless launcher test",
            Type = ProviderType.Docker,
            IsEnabled = true,
        };
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "headless-launcher-test",
            OwnerId = "u",
            Template = template,
            Provider = provider,
            Status = ContainerStatus.Running,
            ExternalId = "headless-launcher-test",
        };
        db.Containers.Add(container);
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "coding",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = container.Id },
        };
        db.Runs.Add(run);
        db.SaveChanges();

        var release = new TaskCompletionSource<HeadlessRunOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Mock<IHeadlessRunner>();
        runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), ConfigPath, It.IsAny<CancellationToken>()))
            .Returns<Run, string, CancellationToken>((_, _, _) =>
            {
                runnerStarted.TrySetResult();
                return release.Task; // blocks until the test releases it
            });

        var services = new ServiceCollection()
            .AddScoped<ContainersDbContext>(_ => InMemoryDbHelper.CreateContext(dbName))
            .AddScoped<IHeadlessRunner>(_ => runner.Object)
            .BuildServiceProvider();
        var launcher = new HeadlessRunLauncher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TestLifetime(),
            NullLogger<HeadlessRunLauncher>.Instance);
        var dispatcher = new RunModeDispatcher(
            db, launcher, Mock.Of<IRunBranchService>(),
            NullLogger<RunModeDispatcher>.Instance);

        var dispatch = dispatcher.DispatchAsync(run, ConfigPath);
        var completedFirst = await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromSeconds(10)));

        completedFirst.Should().BeSameAs(dispatch,
            "the dispatch must return while the agent exec is still in flight");
        (await dispatch).Kind.Should().Be(RunDispatchKind.Detached);

        // The exec genuinely started in the background...
        await runnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        launcher.GetInFlight(run.Id).Should().NotBeNull();

        // ...and completes once the (long) agent run finishes.
        release.SetResult(new HeadlessRunOutcome
        {
            Kind = RunEventKind.Finished,
            Status = RunStatus.Succeeded,
            ExitCode = 0,
        });
        await launcher.GetInFlight(run.Id)!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Launch_ReloadsRunInFreshScope_AndInvokesRunnerWithConfigPath()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = InMemoryDbHelper.CreateContext(dbName);
        var run = SeedRun(db);

        Run? seenByRunner = null;
        var runner = new Mock<IHeadlessRunner>();
        runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), ConfigPath, It.IsAny<CancellationToken>()))
            .Callback<Run, string, CancellationToken>((r, _, _) => seenByRunner = r)
            .ReturnsAsync(new HeadlessRunOutcome { Kind = RunEventKind.Finished, Status = RunStatus.Succeeded });

        var services = new ServiceCollection()
            .AddScoped<ContainersDbContext>(_ => InMemoryDbHelper.CreateContext(dbName))
            .AddScoped<IHeadlessRunner>(_ => runner.Object)
            .BuildServiceProvider();
        var launcher = new HeadlessRunLauncher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TestLifetime(),
            NullLogger<HeadlessRunLauncher>.Instance);

        await launcher.Launch(run.Id, ConfigPath);

        runner.Verify(r => r.StartAsync(
            It.Is<Run>(rn => rn.Id == run.Id && rn.ContainerId == run.ContainerId),
            ConfigPath,
            It.IsAny<CancellationToken>()), Times.Once);
        // The background scope re-loaded its own tracked instance — it must
        // not be the request-scoped entity (whose DbContext is disposed by
        // the time a long run completes).
        seenByRunner.Should().NotBeSameAs(run);
    }

    [Fact]
    public async Task Launch_RunVanished_LogsAndCompletes_WithoutInvokingRunner()
    {
        var runner = new Mock<IHeadlessRunner>();
        var services = new ServiceCollection()
            .AddScoped<ContainersDbContext>(_ => InMemoryDbHelper.CreateContext())
            .AddScoped<IHeadlessRunner>(_ => runner.Object)
            .BuildServiceProvider();
        var launcher = new HeadlessRunLauncher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TestLifetime(),
            NullLogger<HeadlessRunLauncher>.Instance);

        var task = launcher.Launch(Guid.NewGuid(), ConfigPath);
        await task.WaitAsync(TimeSpan.FromSeconds(10));

        task.IsCompletedSuccessfully.Should().BeTrue();
        runner.Verify(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Launch_RunnerThrows_BackgroundTaskCompletesWithoutCrashing()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = InMemoryDbHelper.CreateContext(dbName);
        var run = SeedRun(db);

        var runner = new Mock<IHeadlessRunner>();
        runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));

        var services = new ServiceCollection()
            .AddScoped<ContainersDbContext>(_ => InMemoryDbHelper.CreateContext(dbName))
            .AddScoped<IHeadlessRunner>(_ => runner.Object)
            .BuildServiceProvider();
        var launcher = new HeadlessRunLauncher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TestLifetime(),
            NullLogger<HeadlessRunLauncher>.Instance);

        var task = launcher.Launch(run.Id, ConfigPath);
        await task.WaitAsync(TimeSpan.FromSeconds(10));

        // No unobserved exception escapes — the failure is logged, the run
        // row keeps whatever status the runner last persisted.
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task GetInFlight_RemovesEntry_AfterCompletion()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = InMemoryDbHelper.CreateContext(dbName);
        var run = SeedRun(db);

        var runner = new Mock<IHeadlessRunner>();
        runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeadlessRunOutcome { Kind = RunEventKind.Finished, Status = RunStatus.Succeeded });

        var services = new ServiceCollection()
            .AddScoped<ContainersDbContext>(_ => InMemoryDbHelper.CreateContext(dbName))
            .AddScoped<IHeadlessRunner>(_ => runner.Object)
            .BuildServiceProvider();
        var launcher = new HeadlessRunLauncher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TestLifetime(),
            NullLogger<HeadlessRunLauncher>.Instance);

        var task = launcher.Launch(run.Id, ConfigPath);
        await task.WaitAsync(TimeSpan.FromSeconds(10));

        // The continuation that evicts the entry races the awaited task by
        // design; poll briefly rather than assert instantly.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (launcher.GetInFlight(run.Id) is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        launcher.GetInFlight(run.Id).Should().BeNull();
    }
}

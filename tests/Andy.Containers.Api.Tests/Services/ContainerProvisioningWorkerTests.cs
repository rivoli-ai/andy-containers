using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerProvisioningWorkerTests : IDisposable
{
    private readonly string _dbName;
    private readonly ContainerProvisioningQueue _queue;
    private readonly Mock<IInfrastructureProviderFactory> _mockProviderFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly Mock<IGitCloneService> _mockGitCloneService;
    private readonly Mock<IContainerService> _mockContainerService;
    private readonly Mock<ICodeAssistantInstallService> _mockCodeAssistantInstallService;
    private readonly ServiceProvider _serviceProvider;

    public ContainerProvisioningWorkerTests()
    {
        _dbName = Guid.NewGuid().ToString();
        _queue = new ContainerProvisioningQueue();
        _mockProviderFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();
        _mockProviderFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>())).Returns(_mockProvider.Object);
        _mockGitCloneService = new Mock<IGitCloneService>();
        _mockContainerService = new Mock<IContainerService>();
        _mockCodeAssistantInstallService = new Mock<ICodeAssistantInstallService>();

        // Default: ExecAsync succeeds
        _mockContainerService.Setup(s => s.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var services = new ServiceCollection();
        services.AddDbContext<ContainersDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));
        services.AddScoped<IGitCloneService>(_ => _mockGitCloneService.Object);
        services.AddScoped<IContainerService>(_ => _mockContainerService.Object);
        services.AddScoped<ICodeAssistantInstallService>(_ => _mockCodeAssistantInstallService.Object);
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose() => _serviceProvider.Dispose();

    private ContainersDbContext CreateDb() => InMemoryDbHelper.CreateContext(_dbName);

    private ContainerProvisioningWorker CreateWorker()
    {
        return new ContainerProvisioningWorker(
            _queue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockProviderFactory.Object,
            NullLogger<ContainerProvisioningWorker>.Instance);
    }

    private (Container container, InfrastructureProvider provider) SeedContainerAndProvider(ContainersDbContext db)
    {
        var provider = new InfrastructureProvider
        {
            Code = "docker-local",
            Name = "Local Docker",
            Type = ProviderType.Docker
        };
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test",
            Version = "1.0",
            BaseImage = "ubuntu:24.04"
        };
        var container = new Container
        {
            Name = "test-container",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Pending
        };
        db.Providers.Add(provider);
        db.Templates.Add(template);
        db.Containers.Add(container);
        db.SaveChanges();
        return (container, provider);
    }

    [Fact]
    public async Task ProcessJob_SuccessfulProvision_ShouldSetRunning()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult
            {
                ExternalId = "ext-123",
                Status = ContainerStatus.Running,
                ConnectionInfo = new ConnectionInfo
                {
                    IdeEndpoint = "https://ide.test:8080",
                    VncEndpoint = "https://vnc.test:6080"
                }
            });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start worker, let it process one job, then cancel
        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500); // give it time to process
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Verify
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
        updated.ExternalId.Should().Be("ext-123");
        updated.IdeEndpoint.Should().Be("https://ide.test:8080");
        updated.VncEndpoint.Should().Be("https://vnc.test:6080");
        updated.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessJob_ProviderThrows_ShouldMarkFailed()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon not running"));

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Failed);

        var events = verifyDb.Events.Where(e => e.ContainerId == container.Id).ToList();
        events.Should().Contain(e => e.EventType == ContainerEventType.Failed);
    }

    [Fact]
    public async Task ProcessJob_ContainerNotFound_ShouldSkip()
    {
        using var db = CreateDb();
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        db.Providers.Add(provider);
        db.SaveChanges();

        var job = new ContainerProvisionJob(
            Guid.NewGuid(), provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Should not throw, just skip
        _mockProvider.Verify(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessJob_WithGitRepos_ShouldCloneAfterProvisioning()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, HasGitRepositories: true);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        _mockGitCloneService.Verify(s => s.CloneRepositoriesAsync(container.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_GitCloneFails_ShouldNotFailContainer()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        _mockGitCloneService.Setup(s => s.CloneRepositoriesAsync(container.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Clone failed"));

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, HasGitRepositories: true);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Container should still be Running despite git clone failure
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
    }

    [Fact]
    public async Task ProcessJob_NoGitRepos_ShouldNotCallClone()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, HasGitRepositories: false);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        _mockGitCloneService.Verify(s => s.CloneRepositoriesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverStuckContainers_ShouldMarkOldCreatingAsFailed()
    {
        using var db = CreateDb();
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        var template = new ContainerTemplate { Code = "t", Name = "T", Version = "1.0", BaseImage = "img" };

        var stuckContainer = new Container
        {
            Name = "stuck",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Creating,
            CreatedAt = DateTime.UtcNow.AddMinutes(-60) // well past the 30-minute threshold
        };
        var recentContainer = new Container
        {
            Name = "recent",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Creating,
            CreatedAt = DateTime.UtcNow // just created, should NOT be recovered
        };

        db.Providers.Add(provider);
        db.Templates.Add(template);
        db.Containers.AddRange(stuckContainer, recentContainer);
        db.SaveChanges();

        // Start worker with empty queue — it will run recovery then wait for jobs
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var stuck = await verifyDb.Containers.FindAsync(stuckContainer.Id);
        stuck!.Status.Should().Be(ContainerStatus.Failed);

        var recent = await verifyDb.Containers.FindAsync(recentContainer.Id);
        recent!.Status.Should().Be(ContainerStatus.Creating); // should NOT be recovered
    }

    [Fact]
    public async Task ProcessJob_WithConnectionInfo_ShouldPersistNetworkConfig()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult
            {
                ExternalId = "ext-1",
                Status = ContainerStatus.Running,
                ConnectionInfo = new ConnectionInfo
                {
                    IdeEndpoint = "https://ide:8080",
                    VncEndpoint = "https://vnc:6080",
                    IpAddress = "10.0.0.5"
                }
            });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.NetworkConfig.Should().NotBeNullOrEmpty();
        updated.NetworkConfig.Should().Contain("10.0.0.5");
    }

    [Fact]
    public async Task ProcessJob_ShouldCreateStartedEvent()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var events = verifyDb.Events.Where(e => e.ContainerId == container.Id).ToList();
        events.Should().Contain(e => e.EventType == ContainerEventType.Started);
        events.First(e => e.EventType == ContainerEventType.Started).SubjectId.Should().Be("user1");
    }

    [Fact]
    public async Task ProcessJob_WithPostCreateScripts_ShouldExecWithLongTimeout()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var scripts = new List<string> { "apt-get install -y python3" };
        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, PostCreateScripts: scripts);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Verify ExecAsync was called with the script and a timeout >= 10 minutes
        _mockContainerService.Verify(s => s.ExecAsync(
            container.Id,
            "apt-get install -y python3",
            It.Is<TimeSpan>(t => t >= TimeSpan.FromMinutes(10)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_WithCodeAssistant_ShouldGenerateAndExecInstallScript()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        _mockCodeAssistantInstallService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("npm install -g @anthropic-ai/claude-code");

        var codeAssistant = new CodeAssistantConfig
        {
            Tool = CodeAssistantType.ClaudeCode,
            ApiKeyEnvVar = "ANTHROPIC_API_KEY"
        };

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, CodeAssistant: codeAssistant);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Verify install script was generated
        _mockCodeAssistantInstallService.Verify(s => s.GenerateInstallScript(
            It.Is<CodeAssistantConfig>(c => c.Tool == CodeAssistantType.ClaudeCode)), Times.Once);

        // Verify install script was executed with a timeout >= 10 minutes
        _mockContainerService.Verify(s => s.ExecAsync(
            container.Id,
            "npm install -g @anthropic-ai/claude-code",
            It.Is<TimeSpan>(t => t >= TimeSpan.FromMinutes(10)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_CodeAssistantInstallFails_ShouldNotFailContainer()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        _mockCodeAssistantInstallService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("npm install -g @anthropic-ai/claude-code");

        // ExecAsync for code assistant fails
        _mockContainerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.Is<string>(cmd => cmd.Contains("claude-code")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "npm ERR!" });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, CodeAssistant: new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode });

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Container should still be Running
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
    }

    [Fact]
    public async Task ProcessJob_WithCodeAssistantNull_ShouldNotCallInstall()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null); // No CodeAssistant

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        _mockCodeAssistantInstallService.Verify(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()), Times.Never);
    }

    [Fact]
    public async Task ProcessJob_WithEnvVars_PassesThemViaContainerSpec()
    {
        // Regression test for #123: secrets must be passed via the provider's
        // CreateContainer Env, not exec'd as `echo … >> /etc/environment` which
        // wrote API keys to world-readable files inside the container.
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        ContainerSpec? capturedSpec = null;
        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ContainerSpec, CancellationToken>((spec, _) => capturedSpec = spec)
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var envVars = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test-123" };
        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, EnvironmentVariables: envVars);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Env vars travel via the spec to the provider…
        capturedSpec.Should().NotBeNull();
        capturedSpec!.EnvironmentVariables.Should().ContainKey("ANTHROPIC_API_KEY");
        capturedSpec.EnvironmentVariables!["ANTHROPIC_API_KEY"].Should().Be("sk-test-123");

        // …and are NEVER exec'd as `echo … >> /etc/environment` or similar.
        _mockContainerService.Verify(s => s.ExecAsync(
            container.Id,
            It.Is<string>(cmd =>
                cmd.Contains("ANTHROPIC_API_KEY") &&
                (cmd.Contains("/etc/environment") || cmd.Contains(".bashrc") || cmd.Contains(".profile"))),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessJob_PostCreateAndCodeAssistant_ShouldRunInOrder()
    {
        // Verifies that post-create scripts run before code assistant installation
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        _mockCodeAssistantInstallService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("npm install -g @anthropic-ai/claude-code");

        var callOrder = new List<string>();
        // Match the specific PostCreateScript payload rather than any command
        // containing "apt-get". The welcome-banner script (worker line 302,
        // added in commit 8102a15 for fastfetch) also runs `apt-get install
        // -qq fastfetch`, so the old `Contains("apt-get")` matcher fired
        // twice and produced a false-positive `{post_create, code_assistant,
        // post_create}` order. See rivoli-ai/andy-containers#134.
        _mockContainerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.Is<string>(cmd => cmd.Contains("apt-get install -y python3")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("post_create"))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        _mockContainerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.Is<string>(cmd => cmd.Contains("claude-code")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("code_assistant"))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null,
            PostCreateScripts: new List<string> { "apt-get install -y python3" },
            CodeAssistant: new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode });

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        callOrder.Should().Equal("post_create", "code_assistant");
    }

    [Fact]
    public async Task ProcessJob_WithCodeAssistant_ShouldStayCreatingUntilInstallDone()
    {
        // The container must NOT be Running while scripts/code assistant are still installing.
        // Previously it was set to Running immediately after infrastructure creation,
        // causing users to connect before setup was complete.
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        _mockCodeAssistantInstallService.Setup(s => s.GenerateInstallScript(It.IsAny<CodeAssistantConfig>()))
            .Returns("npm install -g @anthropic-ai/claude-code");

        ContainerStatus? statusDuringInstall = null;
        _mockContainerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.Is<string>(cmd => cmd.Contains("claude-code")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Check what the container status is while the install is running
                using var checkDb = CreateDb();
                var c = checkDb.Containers.Find(container.Id);
                statusDuringInstall = c?.Status;
            })
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null,
            CodeAssistant: new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode });

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // During install, container should be Creating (not Running)
        statusDuringInstall.Should().Be(ContainerStatus.Creating,
            "container must not be Running while code assistant is being installed");

        // After completion, container should be Running
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
        updated.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessJob_WithPostCreateScripts_ShouldStayCreatingDuringScripts()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        ContainerStatus? statusDuringScript = null;
        _mockContainerService.Setup(s => s.ExecAsync(
                It.IsAny<Guid>(), It.Is<string>(cmd => cmd.Contains("apt-get")),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                using var checkDb = CreateDb();
                var c = checkDb.Containers.Find(container.Id);
                statusDuringScript = c?.Status;
            })
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var scripts = new List<string> { "apt-get install -y python3" };
        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, PostCreateScripts: scripts);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        statusDuringScript.Should().Be(ContainerStatus.Creating,
            "container must not be Running while post-create scripts are executing");

        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
    }
}

// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// Cross-process smoke for the AP6 → AQ3 boundary
// (rivoli-ai/andy-containers#143 + rivoli-ai/andy-cli#58, #59).
//
// The unit-level HeadlessRunnerTests cover AP6's logic with a mocked
// IContainerService — exit-code mapping, outbox shape, error paths.
// They never actually spawn andy-cli. This smoke runs the merged AQ3
// binary as a real subprocess via a host-shell IContainerService and
// asserts that:
//
//   1. AP6 builds the right `andy-cli run --headless --config <path>`
//      command.
//   2. The spawned process completes a one-turn LLM exchange via AQ3.
//   3. The exit code (0) round-trips through AP6's exit-code switch as
//      RunEventKind.Finished + RunStatus.Succeeded.
//   4. The outbox row is keyed on Run.Id with subject `…run.{id}.finished`
//      and payload status="Succeeded".
//
// Real LLM call (gpt-4o-mini) — gated behind opt-in env vars so it
// doesn't run in CI without consent.
public class Ap6Aq3SmokeTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly string _tempRoot;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public Ap6Aq3SmokeTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _db = InMemoryDbHelper.CreateContext();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ap6-aq3-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _output = output;
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Ap6Aq3SmokeFact]
    public async Task RealAp6_SpawnsRealAndyCli_PublishesFinishedEvent()
    {
        var cliDll = Environment.GetEnvironmentVariable(Ap6Aq3SmokeFactAttribute.CliDllEnvVar)!;

        var runId = Guid.NewGuid();
        var configPath = Path.Combine(_tempRoot, "config.json");
        var outputPath = Path.Combine(_tempRoot, "output.txt");
        WriteHeadlessConfig(configPath, runId, outputPath);

        var run = new Run
        {
            Id = runId,
            AgentId = "smoke-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            ContainerId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
        };
        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        var hostExec = new HostSubprocessContainerService(cliDll);
        var runner = new HeadlessRunner(
            hostExec, _db, new RunCancellationRegistry(),
            new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance),
            NullLoggerForRunner());

        var outcome = await runner.StartAsync(run, configPath);

        _output.WriteLine($"---andy-cli stdout---\n{hostExec.LastStdOut}");
        _output.WriteLine($"---andy-cli stderr---\n{hostExec.LastStdErr}");
        _output.WriteLine($"---outcome.Error---\n{outcome.Error}");

        outcome.Kind.Should().Be(RunEventKind.Finished,
            $"AQ3's exit 0 must round-trip through AP6 as Finished/Succeeded.\n"
                + $"stdout:\n{hostExec.LastStdOut}\nstderr:\n{hostExec.LastStdErr}");
        outcome.Status.Should().Be(RunStatus.Succeeded);
        outcome.ExitCode.Should().Be(0);

        File.Exists(outputPath).Should().BeTrue(
            "AQ3's atomic output write should produce the agent's response file.");

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().Be($"andy.containers.events.run.{run.Id}.finished",
            "Subject must key on Run.Id with the AQ2 'finished' kind suffix.");
        entry.CorrelationId.Should().Be(run.CorrelationId);

        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.GetProperty("run_id").GetString().Should().Be(run.Id.ToString());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Succeeded");
        doc.RootElement.GetProperty("exit_code").GetInt32().Should().Be(0);
    }

    // Emits an AQ1-schema headless config with empty tools and a tiny
    // OpenAI completion. Kept inline here (not a fixture file) so the
    // smoke is self-contained — anyone reading the test sees the exact
    // shape the spawned binary will load.
    private static void WriteHeadlessConfig(string path, Guid runId, string outputPath)
    {
        var config = new
        {
            schema_version = 1,
            run_id = runId,
            agent = new
            {
                slug = "smoke-agent",
                instructions =
                    "You are a smoke-test agent. Your only job is to reply with the single word DONE — "
                        + "no formatting, no punctuation, just DONE.",
            },
            model = new
            {
                provider = "openai",
                id = "gpt-4o-mini",
                api_key_ref = "env:OPENAI_API_KEY",
            },
            tools = Array.Empty<object>(),
            workspace = new { root = Path.GetDirectoryName(outputPath), branch = "main" },
            output = new { file = outputPath, stream = "stdout" },
            limits = new { max_iterations = 4, timeout_seconds = 60 },
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static Microsoft.Extensions.Logging.ILogger<HeadlessRunner> NullLoggerForRunner()
        => NullLogger<HeadlessRunner>.Instance;

    // IContainerService impl that runs the AP6 spawn command on the host
    // instead of inside Docker. The substitution rule is one-way: AP6
    // builds `andy-cli run --headless --config <path>` and we rewrite
    // `andy-cli` → `dotnet <ANDY_CLI_DLL>`. This deliberately doesn't
    // implement any other IContainerService methods — the smoke only
    // exercises ExecAsync.
    private sealed class HostSubprocessContainerService : IContainerService
    {
        private readonly string _cliDllPath;

        public string? LastStdOut { get; private set; }
        public string? LastStdErr { get; private set; }

        public HostSubprocessContainerService(string cliDllPath) { _cliDllPath = cliDllPath; }

        public Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct = default)
            => ExecAsync(containerId, command, TimeSpan.FromMinutes(15), ct);

        public async Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, CancellationToken ct = default)
        {
            // AP6's command shape (HeadlessRunner.cs:66) — the only one
            // this smoke handles.
            const string prefix = "andy-cli ";
            if (!command.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Smoke ExecAsync only handles `andy-cli …` commands; got: {command}");
            }

            // ProcessStartInfo with ArgumentList=false (we want to honour
            // AP6's POSIX single-quoting) — pass the rest of the command
            // through `/bin/sh -c` so the shell unquotes the path. This
            // is exactly the shell semantics AP6 was designed against.
            // Use the dotnet running this test (Environment.ProcessPath
            // resolves to the same runtime), not whatever's first on PATH —
            // the developer's PATH may resolve to a Homebrew dotnet that
            // doesn't ship .NET 8 (which andy-cli targets).
            var dotnetPath = Environment.ProcessPath ?? "dotnet";
            var argsAfterAndyCli = command[prefix.Length..];
            var shellCommand = $"\"{dotnetPath}\" \"{_cliDllPath}\" {argsAfterAndyCli}";

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", shellCommand },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException("HostSubprocessContainerService ExecAsync timed out.");
            }

            LastStdOut = stdout.ToString();
            LastStdErr = stderr.ToString();
            return new ExecResult
            {
                ExitCode = process.ExitCode,
                StdOut = LastStdOut,
                StdErr = LastStdErr,
            };
        }

        // Methods below are unused by the smoke; fail loudly rather than
        // silently returning defaults if a future change accidentally
        // wires this fake into a code path that needs them.
        public Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task StartContainerAsync(Guid containerId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task StopContainerAsync(Guid containerId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DestroyContainerAsync(Guid containerId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task ResizeContainerAsync(Guid containerId, ResourceSpec resources, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}

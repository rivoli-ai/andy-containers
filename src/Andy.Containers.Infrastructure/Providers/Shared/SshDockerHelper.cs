using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Andy.Containers.Validation;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Andy.Containers.Infrastructure.Providers.Shared;

/// <summary>
/// Shared utility for VM-based providers (Hetzner, DigitalOcean, Civo) that manage
/// Docker containers over SSH connections.
/// </summary>
public class SshDockerHelper : IDisposable
{
    private readonly ILogger _logger;
    private SshClient? _ssh;

    public SshDockerHelper(ILogger logger)
    {
        _logger = logger;
    }

    public void Connect(string host, string username, string privateKeyPath)
    {
        var keyFile = new PrivateKeyFile(privateKeyPath);
        _ssh = new SshClient(host, username, keyFile);
        _ssh.Connect();
        _logger.LogInformation("SSH connected to {Host}", host);
    }

    public void Connect(string host, string username, byte[] privateKeyBytes)
    {
        using var stream = new MemoryStream(privateKeyBytes);
        var keyFile = new PrivateKeyFile(stream);
        _ssh = new SshClient(host, username, keyFile);
        _ssh.Connect();
        _logger.LogInformation("SSH connected to {Host}", host);
    }

    public void ConnectWithPassword(string host, string username, string password)
    {
        _ssh = new SshClient(host, username, password);
        _ssh.Connect();
        _logger.LogInformation("SSH connected to {Host} with password auth", host);
    }

    public Task<string> RunContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        EnsureConnected();

        // rivoli-ai/andy-containers#128. Validate the image reference at
        // the boundary; defense-in-depth on top of the shell-quoting
        // applied below. Catches obviously malformed BaseImage values
        // before they hit a remote shell.
        OciReferenceValidator.Validate(spec.ImageReference, paramName: nameof(spec.ImageReference));

        var containerName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";

        var cmd = BuildRunCommand(spec, containerName);

        _logger.LogInformation("Running Docker container via SSH: {Cmd}", cmd);
        var result = RunCommand(cmd);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start container: {result.StdErr}");

        return Task.FromResult(containerName);
    }

    /// <summary>
    /// Build the <c>docker run -d ...</c> command we send over SSH for
    /// <see cref="RunContainerAsync"/>. Pure function so the assembly
    /// can be unit-tested without a live SSH connection
    /// (rivoli-ai/andy-containers#128). Every interpolated value is
    /// POSIX-quoted before insertion: a remote shell session
    /// re-tokenises the command we send, so an unquoted env value or
    /// image ref with whitespace would split into independent argv
    /// tokens just like the local-process case.
    /// </summary>
    internal static string BuildRunCommand(ContainerSpec spec, string containerName)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(containerName);

        var envArgs = "";
        if (spec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
            {
                // Quote both the KEY=VALUE token (because VALUE may
                // contain shell metas) and treat the entire string as
                // one argv slot. KEY itself is operator-controlled and
                // a sane shell-safe identifier in practice; quoting
                // the whole pair is the conservative move.
                envArgs += $" -e {PosixShellQuote.Quote($"{key}={value ?? string.Empty}")}";
            }
        }

        var portArgs = "";
        if (spec.PortMappings is not null)
        {
            foreach (var (container, host) in spec.PortMappings)
            {
                // Ports are integers; quoting is unnecessary but
                // costs nothing and keeps the rule "every interpolated
                // value is quoted" easy to enforce by review.
                portArgs += $" -p {PosixShellQuote.Quote($"{host}:{container}")}";
            }
        }

        var resources = spec.Resources ?? new ResourceSpec();
        var cpuLimit = $"--cpus={resources.CpuCores}";
        var memLimit = $"--memory={resources.MemoryMb}m";

        // Mirror the local DockerInfrastructureProvider hardening: PID cap to
        // resist fork bombs, no-new-privileges to block setuid escalation, and
        // drop NET_RAW + MKNOD which dev workloads do not need.
        const string hardening =
            " --pids-limit 4096" +
            " --security-opt no-new-privileges" +
            " --cap-drop NET_RAW --cap-drop MKNOD";

        var cmd =
            $"docker run -d --name {PosixShellQuote.Quote(containerName)}" +
            $" {cpuLimit} {memLimit}{hardening}{envArgs}{portArgs}" +
            $" {PosixShellQuote.Quote(spec.ImageReference)}";

        if (!string.IsNullOrEmpty(spec.Command))
        {
            cmd += $" {PosixShellQuote.Quote(spec.Command)}";
            if (spec.Arguments is not null)
            {
                foreach (var arg in spec.Arguments)
                {
                    cmd += $" {PosixShellQuote.Quote(arg)}";
                }
            }
        }

        return cmd;
    }

    public ExecResult DockerExec(string containerName, string command)
    {
        EnsureConnected();
        // rivoli-ai/andy-containers#128. Quote both the container name
        // and the inner command. The previous bespoke 's/'/'\\''/g'
        // replacement was correct for the inner command but didn't
        // protect the container name against an exotic externalId.
        return RunCommand(
            $"docker exec {PosixShellQuote.Quote(containerName)} sh -c {PosixShellQuote.Quote(command)}");
    }

    public void StopContainer(string containerName)
    {
        EnsureConnected();
        // rivoli-ai/andy-containers#128. Container names are operator-
        // generated alphanumerics today, but quoting is the consistent
        // rule across the helper.
        RunCommand($"docker stop {PosixShellQuote.Quote(containerName)}");
    }

    public void RemoveContainer(string containerName)
    {
        EnsureConnected();
        RunCommand($"docker rm -f {PosixShellQuote.Quote(containerName)}");
    }

    public ContainerRuntimeInfo GetContainerInfo(string containerName)
    {
        EnsureConnected();
        // rivoli-ai/andy-containers#128. The --format pattern is a
        // fixed literal authored by us — safe to interpolate without
        // quoting. The container name is the untrusted value here.
        var result = RunCommand(
            $"docker inspect --format '{{{{.State.Running}}}} {{{{.State.StartedAt}}}}' {PosixShellQuote.Quote(containerName)}");
        var parts = result.StdOut?.Trim().Split(' ') ?? [];
        var running = parts.Length > 0 && parts[0] == "true";

        return new ContainerRuntimeInfo
        {
            ExternalId = containerName,
            Status = running ? ContainerStatus.Running : ContainerStatus.Stopped,
            StartedAt = parts.Length > 1 && DateTime.TryParse(parts[1], out var started) ? started : null
        };
    }

    public ExecResult RunCommand(string command)
    {
        EnsureConnected();
        using var cmd = _ssh!.CreateCommand(command);
        cmd.Execute();

        return new ExecResult
        {
            ExitCode = cmd.ExitStatus ?? -1,
            StdOut = cmd.Result,
            StdErr = cmd.Error
        };
    }

    public static string GetCloudInitScript(string dockerImage, string containerName, Dictionary<int, int>? portMappings)
    {
        // rivoli-ai/andy-containers#128. The cloud-init script runs
        // verbatim on the VM at first boot — every interpolated value
        // sits inside a bash command that the kernel re-tokenises.
        // Quote every variable; validate the image reference up-front
        // for the same defense-in-depth reasoning as RunContainerAsync.
        OciReferenceValidator.Validate(dockerImage, paramName: nameof(dockerImage));
        ArgumentException.ThrowIfNullOrEmpty(containerName);

        var portArgs = "";
        if (portMappings is not null)
        {
            foreach (var (container, host) in portMappings)
                portArgs += $" -p {PosixShellQuote.Quote($"{host}:{container}")}";
        }

        var quotedImage = PosixShellQuote.Quote(dockerImage);
        var quotedName = PosixShellQuote.Quote(containerName);

        return $"""
                #!/bin/bash
                set -e
                apt-get update -qq
                apt-get install -y -qq docker.io
                systemctl enable docker
                systemctl start docker
                docker pull {quotedImage}
                docker run -d --name {quotedName} --restart unless-stopped --pids-limit 4096 --security-opt no-new-privileges --cap-drop NET_RAW --cap-drop MKNOD{portArgs} {quotedImage}
                """;
    }

    private void EnsureConnected()
    {
        if (_ssh is null || !_ssh.IsConnected)
            throw new InvalidOperationException("SSH connection is not established. Call Connect() first.");
    }

    public void Dispose()
    {
        _ssh?.Dispose();
    }
}

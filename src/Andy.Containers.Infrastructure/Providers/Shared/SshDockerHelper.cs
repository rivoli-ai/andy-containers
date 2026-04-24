using Andy.Containers.Abstractions;
using Andy.Containers.Models;
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
        var containerName = $"andy-{Guid.NewGuid().ToString("N")[..12]}";

        var envArgs = "";
        if (spec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in spec.EnvironmentVariables)
                envArgs += $" -e {key}={value}";
        }

        var portArgs = "";
        if (spec.PortMappings is not null)
        {
            foreach (var (container, host) in spec.PortMappings)
                portArgs += $" -p {host}:{container}";
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

        var cmd = $"docker run -d --name {containerName} {cpuLimit} {memLimit}{hardening}{envArgs}{portArgs} {spec.ImageReference}";
        if (!string.IsNullOrEmpty(spec.Command))
        {
            cmd += $" {spec.Command}";
            if (spec.Arguments is not null)
                cmd += " " + string.Join(" ", spec.Arguments);
        }

        _logger.LogInformation("Running Docker container via SSH: {Cmd}", cmd);
        var result = RunCommand(cmd);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start container: {result.StdErr}");

        return Task.FromResult(containerName);
    }

    public ExecResult DockerExec(string containerName, string command)
    {
        EnsureConnected();
        return RunCommand($"docker exec {containerName} sh -c '{command.Replace("'", "'\\''")}'");
    }

    public void StopContainer(string containerName)
    {
        EnsureConnected();
        RunCommand($"docker stop {containerName}");
    }

    public void RemoveContainer(string containerName)
    {
        EnsureConnected();
        RunCommand($"docker rm -f {containerName}");
    }

    public ContainerRuntimeInfo GetContainerInfo(string containerName)
    {
        EnsureConnected();
        var result = RunCommand($"docker inspect --format '{{{{.State.Running}}}} {{{{.State.StartedAt}}}}' {containerName}");
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
        var portArgs = "";
        if (portMappings is not null)
        {
            foreach (var (container, host) in portMappings)
                portArgs += $" -p {host}:{container}";
        }

        return $"""
                #!/bin/bash
                set -e
                apt-get update -qq
                apt-get install -y -qq docker.io
                systemctl enable docker
                systemctl start docker
                docker pull {dockerImage}
                docker run -d --name {containerName} --restart unless-stopped --pids-limit 4096 --security-opt no-new-privileges --cap-drop NET_RAW --cap-drop MKNOD{portArgs} {dockerImage}
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

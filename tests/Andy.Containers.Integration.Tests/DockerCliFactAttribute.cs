// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Xunit;

namespace Andy.Containers.Integration.Tests;

// Fact attribute that skips when the Docker CLI / daemon is not available.
// DockerInfrastructureProvider integration tests create real containers and
// `docker exec` into them; without a reachable daemon they fail with a
// connection error rather than a clean skip. Mirrors
// AppleContainerCliFactAttribute's `which`-probe pattern, but also confirms
// the daemon actually answers (`docker info`) — a CLI on PATH with a dead
// daemon would otherwise let the test start and then explode mid-run.
//
// Cached at process scope so a developer running `dotnet test` repeatedly
// doesn't pay the probe cost on every fact.
public sealed class DockerCliFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> _dockerReady = new(ProbeForDocker);

    public DockerCliFactAttribute()
    {
        if (!_dockerReady.Value)
        {
            Skip = "Docker daemon not reachable (`docker info` failed); skipping integration test " +
                   "that creates real containers. Start Docker Desktop or colima to enable it.";
        }
    }

    private static bool ProbeForDocker()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            if (!process.WaitForExit(milliseconds: 5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            // No docker on PATH, Win32Exception, etc. → treat as "not
            // available" and skip rather than crashing the test infra.
            return false;
        }
    }
}

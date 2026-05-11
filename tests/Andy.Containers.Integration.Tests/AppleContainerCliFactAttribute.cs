// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Xunit;

namespace Andy.Containers.Integration.Tests;

// Fact attribute that skips when Apple's `container` CLI is not
// installed on PATH. AppleContainerProvider integration tests spawn
// the CLI directly; without it they fail with a Win32Exception ("No
// such file or directory") rather than a clean skip. Mirrors
// NatsFactAttribute's env-var gate pattern.
//
// Detection is `which container` on Unix-like systems. Cached at
// process scope so a developer running `dotnet test` repeatedly
// doesn't pay the probe cost on every fact.
public sealed class AppleContainerCliFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> _cliPresent = new(ProbeForCli);

    public AppleContainerCliFactAttribute()
    {
        if (!_cliPresent.Value)
        {
            Skip = "Apple `container` CLI not on PATH; skipping integration test that requires it. " +
                   "Install Apple's container runtime (https://github.com/apple/container) and run " +
                   "`container system start` to enable these tests.";
        }
    }

    private static bool ProbeForCli()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/which",
                Arguments = "container",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            process.WaitForExit(milliseconds: 1000);
            return process.ExitCode == 0;
        }
        catch
        {
            // Anything goes wrong (no `which`, no /usr/bin/which on
            // Windows, etc.) → treat as "not present" and skip rather
            // than crashing the test infra.
            return false;
        }
    }
}

// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace Andy.Containers.Integration.Tests.TestSupport;

/// <summary>
/// xUnit fixture that boots a zot OCI registry in a Docker container
/// for the duration of a test class. Used by IM11 (#265) to exercise
/// <c>LocalZotAdapter</c> against a real registry rather than HTTP
/// stubs.
/// </summary>
/// <remarks>
/// <para>
/// Skipped when Docker is unavailable. Test classes consume this via
/// <see cref="IClassFixture{T}"/> and inspect <see cref="IsAvailable"/>
/// at the top of each test, returning early if false. CI environments
/// without Docker still see the class compile and the rest of the
/// suite run.
/// </para>
/// <para>
/// Pulls <c>ghcr.io/project-zot/zot-minimal-linux-{arch}:v2.1.16</c> —
/// the same image Conductor's <c>scripts/fetch-zot.sh</c> bundles, so
/// the test exercises the registry binary that ships in production.
/// </para>
/// </remarks>
public sealed class ZotContainerFixture : IAsyncLifetime
{
    private const string ZotImageVersion = "v2.1.16";
    private string? _containerId;
    private int _hostPort;

    public bool IsAvailable { get; private set; }
    public string BaseUrl { get; private set; } = "http://localhost:5050";

    public async Task InitializeAsync()
    {
        if (!await DockerIsHealthyAsync())
        {
            IsAvailable = false;
            return;
        }

        _hostPort = PickFreePort();
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            _ => "amd64",
        };
        var image = $"ghcr.io/project-zot/zot-minimal-linux-{arch}:{ZotImageVersion}";

        var run = await RunDockerAsync(
            "run", "-d",
            "-p", $"{_hostPort}:5000",
            "--rm",
            image);
        if (run.ExitCode != 0)
        {
            // Pull may have failed (rate limit, offline) — skip
            // rather than fail the test class.
            IsAvailable = false;
            return;
        }
        _containerId = run.Stdout.Trim();
        BaseUrl = $"http://localhost:{_hostPort}";

        // Wait for /v2/ to become reachable. zot needs a beat after
        // `docker run` to bind its listener.
        if (!await WaitForReadyAsync(TimeSpan.FromSeconds(20)))
        {
            // Timed out — kill the container and skip.
            await StopAsync();
            IsAvailable = false;
            return;
        }

        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
    }

    private async Task StopAsync()
    {
        if (_containerId is null) return;
        try
        {
            await RunDockerAsync("kill", _containerId);
        }
        catch (Exception)
        {
            // Best-effort cleanup; if `docker kill` fails the
            // --rm flag still tears the container down on exit.
        }
        _containerId = null;
    }

    private async Task<bool> DockerIsHealthyAsync()
    {
        try
        {
            var info = await RunDockerAsync("info", "--format", "{{.ServerVersion}}");
            return info.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> WaitForReadyAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await http.GetAsync("v2/");
                if (resp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Connection refused while zot is booting — retry.
            }
            await Task.Delay(250);
        }
        return false;
    }

    private static int PickFreePort()
    {
        // Bind a TcpListener on port 0 to ask the OS for a free
        // port, capture the port, release. Race-prone (the port may
        // be taken between release and Docker grabbing it) but rare
        // enough in practice for a one-shot test fixture.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }
}

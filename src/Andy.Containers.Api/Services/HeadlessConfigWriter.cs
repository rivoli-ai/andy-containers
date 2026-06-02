using Andy.Containers.Configurator;
using Microsoft.Extensions.Configuration;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) writer. The on-disk runs-root is
/// config-driven via the <c>ANDY_HEADLESS_RUNS_ROOT</c> environment variable
/// (or <c>Containers:HeadlessRunsRoot</c> setting). When unset it defaults to
/// a user-writable path under the OS temp dir so an unsandboxed host process
/// (the Conductor daemon on macOS) never tries to <c>mkdir</c> under the
/// root-owned <c>/var/run</c>. Hosted/Docker deployments point the env var at
/// <c>/var/run/andy/runs</c> explicitly. This replaces the former
/// <c>IsEmbedded()</c> branch — the "Embedded" hosting environment is being
/// retired across the services (the daemon now runs them as host processes,
/// not in-process), so runtime behaviour is selected by explicit config, not
/// by an environment name.
/// </summary>
public sealed class HeadlessConfigWriter : IHeadlessConfigWriter
{
    private const string ConfigFileName = "config.json";
    private const string RunsRootEnvVar = "ANDY_HEADLESS_RUNS_ROOT";
    private const string RunsRootConfigKey = "Containers:HeadlessRunsRoot";

    private readonly string _runsRoot;

    public HeadlessConfigWriter(IConfiguration configuration)
    {
        // Explicit config wins (env var or appsettings); otherwise default to
        // a user-writable temp location that works for the host daemon and is
        // shareable with the local container runtime.
        var configured =
            Environment.GetEnvironmentVariable(RunsRootEnvVar)
            ?? configuration?[RunsRootConfigKey];

        _runsRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "andy-containers", "runs")
            : configured;
    }

    public async Task<string> WriteAsync(HeadlessRunConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.RunId == Guid.Empty)
        {
            throw new ArgumentException("HeadlessRunConfig.RunId must be set before writing.", nameof(config));
        }

        var runDir = Path.Combine(_runsRoot, config.RunId.ToString());
        Directory.CreateDirectory(runDir);

        var path = Path.Combine(runDir, ConfigFileName);
        var json = HeadlessConfigJson.Serialize(config);

        // Atomic write via tmp + rename — the AQ1 runtime spec calls out
        // the same pattern for output.file. AP6 may pick up the path the
        // moment we return, so a half-written file on a crash would be
        // worse than no file at all.
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, ct);
        File.Move(tmpPath, path, overwrite: true);

        return path;
    }
}

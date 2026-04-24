using Andy.Containers.Configurator;
using Microsoft.Extensions.Hosting;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) writer. Uses
/// <see cref="HostEnvironmentExtensions.IsEmbedded"/> to choose the on-disk
/// root: hosted/Docker writes under <c>/var/run/andy/runs</c>, embedded
/// (Conductor) writes under the OS temp dir so the unsandboxed Mac binary
/// doesn't try to mkdir under <c>/var</c>.
/// </summary>
public sealed class HeadlessConfigWriter : IHeadlessConfigWriter
{
    private const string HostedRoot = "/var/run/andy/runs";
    private const string ConfigFileName = "config.json";

    private readonly IHostEnvironment _environment;

    public HeadlessConfigWriter(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<string> WriteAsync(HeadlessRunConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.RunId == Guid.Empty)
        {
            throw new ArgumentException("HeadlessRunConfig.RunId must be set before writing.", nameof(config));
        }

        var root = _environment.IsEmbedded()
            ? Path.Combine(Path.GetTempPath(), "andy-containers", "runs")
            : HostedRoot;

        var runDir = Path.Combine(root, config.RunId.ToString());
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

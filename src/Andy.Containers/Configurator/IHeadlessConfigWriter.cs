namespace Andy.Containers.Configurator;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105). Persists a built
/// <see cref="HeadlessRunConfig"/> to disk so AP6 can hand the file path to
/// <c>andy-cli run --headless --config &lt;path&gt;</c>. AP3 only writes the
/// file; AP6 owns process spawning.
/// </summary>
public interface IHeadlessConfigWriter
{
    /// <summary>
    /// Serializes <paramref name="config"/> as snake_case JSON and writes it
    /// to a per-run path under the configurator root. Returns the absolute
    /// path written. Idempotent — overwrites any existing file for the same
    /// <c>RunId</c> (re-runs of AP6 against the same Run are expected to pick
    /// up updated config).
    /// </summary>
    Task<string> WriteAsync(HeadlessRunConfig config, CancellationToken ct = default);
}

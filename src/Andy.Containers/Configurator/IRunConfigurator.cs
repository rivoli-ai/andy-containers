using Andy.Containers.Models;

namespace Andy.Containers.Configurator;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105) facade: fetch agent spec → build
/// headless config → write to disk. Single entry point so the AP2 controller
/// (and future AP5 dispatcher) doesn't have to wire the three steps itself.
/// </summary>
public interface IRunConfigurator
{
    Task<RunConfiguratorResult> ConfigureAsync(Run run, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a configurator pass. <see cref="Path"/> is the absolute config
/// path on success; on failure it's null and <see cref="Error"/> explains why.
/// AP3 does not throw on agent-lookup misses or builder validation errors —
/// the caller decides whether to mark the Run as Failed or just log + skip.
/// </summary>
public sealed record RunConfiguratorResult
{
    public string? Path { get; init; }
    public string? Error { get; init; }

    public bool IsSuccess => Path is not null && Error is null;

    public static RunConfiguratorResult Ok(string path) => new() { Path = path };
    public static RunConfiguratorResult Fail(string error) => new() { Error = error };
}

using Andy.Containers.Models;

namespace Andy.Containers.Configurator;

/// <summary>
/// AP3 (rivoli-ai/andy-containers#105). Maps a <see cref="Run"/> + resolved
/// <see cref="AgentSpec"/> into a <see cref="HeadlessRunConfig"/> shaped to
/// the AQ1 schema. Throws <see cref="ArgumentException"/> when the inputs
/// can't produce a schema-valid config (unknown provider, malformed tool
/// binding, empty instructions) — caller decides whether to surface that
/// as a 400 or a transition to <see cref="RunStatus.Failed"/>.
/// </summary>
public interface IHeadlessConfigBuilder
{
    HeadlessRunConfig Build(Run run, AgentSpec agent);
}

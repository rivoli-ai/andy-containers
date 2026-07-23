// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// Walks the well-known outputs directory inside a container and
/// produces a structured manifest of artifacts to publish on the
/// terminal run event.
/// Successful agent runs may first materialize commit-derived deliverables
/// beneath the same root; the recursive walk treats those patch/manifest
/// files exactly like agent-declared outputs.
///
/// Issue rivoli-ai/andy-containers#316. The default contract:
///
/// <list type="bullet">
///   <item>Scan path is <c>/workspace/.andy/outputs/</c> inside the
///   container.</item>
///   <item>Walk is recursive. <see cref="RunOutputArtifact.RelativePath"/>
///   is the file's path relative to that root, forward-slash
///   separated.</item>
///   <item>Missing directory → empty list (no error).</item>
///   <item>Any internal failure must surface as an empty list and a
///   logged warning — artifact collection is best-effort and must
///   never block the terminal event from being written.</item>
/// </list>
///
/// Implementations are free to read the filesystem directly (when the
/// workspace is bind-mounted host-side) or shell out via
/// <see cref="IInfrastructureProvider.ExecAsync(string, string, CancellationToken)"/>
/// to inspect the container in-band. The default implementation uses
/// the exec path so it works for every provider that supports
/// <see cref="ProviderCapabilities.SupportsExec"/>.
/// </summary>
public interface IOutputArtifactCollector
{
    Task<IReadOnlyList<RunOutputArtifact>> CollectAsync(
        Container container,
        CancellationToken ct = default);

    /// <summary>
    /// Collect artifacts for a concrete agent run. Implementations that upload
    /// bytes can attach document links to <paramref name="runId"/> instead of
    /// incorrectly treating the hosting container id as the run identity.
    /// </summary>
    Task<IReadOnlyList<RunOutputArtifact>> CollectRunAsync(
        Container container,
        Guid runId,
        CancellationToken ct = default)
        => CollectAsync(container, ct);
}

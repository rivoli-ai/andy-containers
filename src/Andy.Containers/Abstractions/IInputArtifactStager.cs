// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Configurator;
using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// EX.7 (rivoli-ai/andy-containers#328). Stages cross-container input
/// artifacts into a container's well-known inputs directory
/// (<c>/workspace/.andy/inputs/</c>) BEFORE the agent starts. Inverse of
/// <see cref="IOutputArtifactCollector"/>: outputs are collected and pushed
/// to andy-docs at terminal-event time; inputs are pulled from andy-docs
/// and written into the container at run-start time.
///
/// <para>
/// <strong>Failure semantics:</strong> unlike output collection (best
/// effort, never blocks), input staging is on the run-START critical path.
/// A missing, oversized, or failed input fetch MUST fail the run rather
/// than start the agent against an empty input. Implementations therefore
/// throw <see cref="InputStagingException"/> on any input that cannot be
/// staged; the runner maps that to a Failed run with a clear, typed error.
/// </para>
///
/// <para>
/// No inputs (<c>null</c>/empty list) → no-op, no error, no exec round-trip
/// — behaviour identical to pre-EX.7.
/// </para>
/// </summary>
public interface IInputArtifactStager
{
    /// <summary>
    /// Download each input's andy-docs document and write its bytes under
    /// <c>/workspace/.andy/inputs/&lt;dest_relative_path&gt;</c> inside the
    /// container. Throws <see cref="InputStagingException"/> if any input
    /// is missing, oversized, or otherwise un-fetchable.
    /// </summary>
    Task StageAsync(
        Container container,
        IReadOnlyList<HeadlessInput> inputs,
        CancellationToken ct = default);
}

/// <summary>
/// EX.7 typed failure: an input artifact could not be staged, so the run
/// must not start. Carries the offending document id and the failure class
/// so the runner can surface an actionable error.
/// </summary>
public sealed class InputStagingException : Exception
{
    public Guid DocsRef { get; }
    public string DestRelativePath { get; }
    public InputStagingFailure Failure { get; }

    public InputStagingException(
        Guid docsRef, string destRelativePath, InputStagingFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        DocsRef = docsRef;
        DestRelativePath = destRelativePath;
        Failure = failure;
    }
}

/// <summary>EX.7 input-staging failure classes.</summary>
public enum InputStagingFailure
{
    /// <summary>The andy-docs document (or its head version) was not found.</summary>
    NotFound,

    /// <summary>The document exceeds the configured input size cap.</summary>
    TooLarge,

    /// <summary>Transient / unexpected fetch failure (network, 5xx, timeout, mis-shaped body).</summary>
    FetchFailed,

    /// <summary>The bytes could not be written into the container (exec error, non-zero exit).</summary>
    WriteFailed,

    /// <summary>No andy-docs client is configured but inputs were declared — staging is impossible.</summary>
    DocsClientUnavailable,
}

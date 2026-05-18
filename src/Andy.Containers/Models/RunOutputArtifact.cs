// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Models;

/// <summary>
/// One file produced by an agent run, surfaced on the terminal
/// <c>andy.containers.events.run.{id}.*</c> event payload and on the
/// persisted <see cref="Run.OutputArtifacts"/> column.
///
/// Issue rivoli-ai/andy-containers#316. Consumed by andy-tasks#275
/// (upload to andy-docs + persist as <c>TaskNode.OutputDocRefs</c>) and
/// by Conductor's <c>TaskArtifactsCardView</c> via that consumer.
/// </summary>
/// <param name="Name">
/// Basename of the produced file (e.g. <c>report.pdf</c>). Useful for
/// display when the relative path is uninformative.
/// </param>
/// <param name="RelativePath">
/// Path of the file relative to the agent's well-known outputs root
/// (<c>/workspace/.andy/outputs/</c>). Forward-slash separated.
/// </param>
/// <param name="SizeBytes">
/// File size in bytes at collection time. Captured at terminal so a
/// late mutation by the agent post-exit is observed.
/// </param>
/// <param name="Sha256">
/// Hex-encoded SHA-256 of the file contents. Lower-case, no leading
/// "sha256:" prefix — consumers add their own scheme tag if needed.
/// </param>
/// <param name="ContentType">
/// MIME type guessed from the filename extension. Null when the
/// extension is unknown or the file has none.
/// </param>
public sealed record RunOutputArtifact(
    string Name,
    string RelativePath,
    long SizeBytes,
    string Sha256,
    string? ContentType);

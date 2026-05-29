// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Models;

/// <summary>
/// EX.7 (rivoli-ai/andy-containers#328). One declared cross-container
/// input for a run: an andy-docs document to fetch and a destination path
/// (relative to <c>/workspace/.andy/inputs/</c>) to stage it at inside the
/// container before the agent starts.
///
/// <para>
/// This is the inverse of <see cref="RunOutputArtifact"/>: outputs are
/// <em>collected</em> from <c>/workspace/.andy/outputs/</c> and pushed to
/// andy-docs at terminal-event time; inputs are <em>pulled</em> from
/// andy-docs and staged under <c>/workspace/.andy/inputs/</c> at run-start
/// time. Together they form the cross-container artifact-handoff path
/// consumed by andy-tasks EX.4 (ContextHandoffBuilder).
/// </para>
///
/// <para>
/// <see cref="DocsRef"/> is the andy-docs document id (the
/// <c>DocumentId</c> half of a <see cref="Models.DocsRef"/>); the stager
/// resolves the document's head content-hash and downloads that blob.
/// <see cref="DestRelativePath"/> must be a normalised relative path — the
/// configurator rejects absolute paths and <c>..</c> traversal at
/// config-build time so a malformed handoff fails the run start rather
/// than escaping the inputs root.
/// </para>
/// </summary>
public sealed record RunInput(Guid DocsRef, string DestRelativePath);

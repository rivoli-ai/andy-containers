// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Models;

/// <summary>
/// rivoli-ai/andy-containers#320. Canonical pointer into andy-docs for a
/// piece of uploaded content. Stamped onto
/// <see cref="RunOutputArtifact.DocsRef"/> after a successful
/// <c>POST /api/documents:put</c>; <c>null</c> when the andy-docs upload
/// was skipped or failed (best-effort / metadata-only fallback).
///
/// <para>
/// Lives in <c>Andy.Containers.Models</c> rather than the infrastructure
/// layer so it can ride on the wire payload (<c>RunEventPayload</c>) and
/// the persisted entity (<c>Run.OutputArtifacts</c>) without forcing
/// either to depend on infrastructure. The HTTP-client surface in
/// <c>Andy.Containers.Infrastructure.Audit</c> projects onto this type
/// after parsing andy-docs's response DTO.
/// </para>
///
/// <para>
/// Mirrors andy-docs's domain <c>DocsRef</c> value object on the wire:
/// <c>DocumentId</c> is the content-addressed document id (stable across
/// re-uploads of identical bytes), <c>LinkId</c> is the per-target link
/// id (one per <c>(documentId, target, role)</c> tuple).
/// </para>
/// </summary>
public sealed record DocsRef(Guid DocumentId, Guid LinkId);

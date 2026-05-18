// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Audit;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Default <see cref="IOutputArtifactCollector"/> for rivoli-ai/andy-containers#316.
///
/// Walks the agent's well-known outputs root inside the container by
/// shelling out via <see cref="IContainerService.ExecAsync(Guid, string, TimeSpan, CancellationToken)"/>.
/// Using the exec path (rather than a host bind-mount probe) makes the
/// collector provider-agnostic — every provider that supports run
/// execution can also support artifact collection without per-runtime
/// workspace-path resolution.
///
/// The probe shell pipeline is one command:
///
/// <code>
/// test -d /workspace/.andy/outputs &amp;&amp; \
///   find /workspace/.andy/outputs -type f -print0 | \
///   xargs -0 -I{} sh -c 'sz=$(stat -c %s "{}"); h=$(sha256sum "{}" | awk "{print \$1}"); echo "$sz\t$h\t{}"'
/// </code>
///
/// Output is one record per line, TAB-separated:
/// <c>size_bytes \t sha256 \t absolute_path</c>. We re-base
/// <c>absolute_path</c> onto the outputs root to get the relative path.
///
/// All failures (exec error, non-zero exit, parse trouble, hashing
/// trouble) collapse to "empty list + logged warning" — by contract,
/// artifact collection must never block the terminal event. See
/// <see cref="IOutputArtifactCollector"/>.
///
/// TODO #316.B: when a future Container.WorkspaceHostPath column lands
/// (bind-mount-aware providers), short-circuit the exec round-trip and
/// walk the host filesystem directly for those providers. The
/// interface contract is the same either way; only the implementation
/// differs.
/// </summary>
public sealed class FilesystemOutputArtifactCollector : IOutputArtifactCollector
{
    // Pinned by design in the issue (#316). The agent SDK plants files
    // here; consumers know to look here. Changing it requires a
    // coordinated bump on the agent side.
    public const string OutputsRoot = "/workspace/.andy/outputs";

    // Cap collection wall-clock so a misbehaving container can't wedge
    // the terminal-event write. 30s is generous — sha256sum at ~500MB/s
    // covers ~15GB of artifacts before we trip, which is well above any
    // realistic agent output budget.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // rivoli-ai/andy-containers#320. Wall-clock cap on the per-file
    // base64 read. Keep distinct from ProbeTimeout so a large artifact
    // can't burn the whole probe budget on its first file. 60s
    // accommodates a multi-hundred-MB artifact streamed through the
    // exec channel; smaller files are the common case.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    // rivoli-ai/andy-containers#320. Hard upload cap per artifact. The
    // exec-channel base64 round-trip materialises the full payload in
    // memory twice (once as base64 stdout, once as the decoded byte[]),
    // so we cap at 64MiB to keep peak RSS bounded even when an agent
    // produces a pathologically large file. Larger artifacts log a
    // warning and are emitted metadata-only (no DocsRef).
    private const long MaxUploadSizeBytes = 64L * 1024 * 1024;

    private readonly IContainerService _containers;
    private readonly IAndyDocsClient? _andyDocs;
    private readonly ILogger<FilesystemOutputArtifactCollector> _logger;

    public FilesystemOutputArtifactCollector(
        IContainerService containers,
        ILogger<FilesystemOutputArtifactCollector> logger,
        IAndyDocsClient? andyDocs = null)
    {
        _containers = containers;
        _logger = logger;
        // rivoli-ai/andy-containers#320. Optional. When null, the
        // collector operates in pre-#320 metadata-only mode — every
        // emitted artifact has DocsRef=null. Live DI registers the
        // client iff AndyDocs:ApiBaseUrl is set, so dev / embedded
        // setups without andy-docs degrade cleanly.
        _andyDocs = andyDocs;
    }

    public async Task<IReadOnlyList<RunOutputArtifact>> CollectAsync(
        Container container,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        // No external id → container never reached a runnable state.
        // Nothing to scan; skip silently rather than fabricating an
        // exec failure.
        if (string.IsNullOrEmpty(container.ExternalId))
        {
            return Array.Empty<RunOutputArtifact>();
        }

        try
        {
            // -print0 / xargs -0 keeps spaces and quotes in filenames
            // intact. The inner shell prints size, sha256, and the
            // file path, tab-separated. `2>/dev/null` swallows
            // permission-denied / vanished-file noise that would
            // otherwise show up on stderr without changing the
            // semantically-empty success case.
            var script = $"if [ -d {OutputsRoot} ]; then " +
                "find " + OutputsRoot + " -type f -print0 2>/dev/null | " +
                "xargs -0 -I{} sh -c 'sz=$(stat -c %s \"{}\" 2>/dev/null); " +
                "h=$(sha256sum \"{}\" 2>/dev/null | awk \"{print \\$1}\"); " +
                "[ -n \"$sz\" ] && [ -n \"$h\" ] && printf \"%s\\t%s\\t%s\\n\" \"$sz\" \"$h\" \"{}\"'; " +
                "fi";

            var result = await _containers.ExecAsync(
                container.Id, $"sh -c '{script.Replace("'", "'\\''")}'", ProbeTimeout, ct);

            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Artifact probe in container {ContainerId} exited with code {ExitCode}; treating as empty manifest. StdErr: {StdErr}",
                    container.Id, result.ExitCode, Truncate(result.StdErr, 500));
                return Array.Empty<RunOutputArtifact>();
            }

            var parsed = ParseProbeOutput(result.StdOut);

            // rivoli-ai/andy-containers#320. After enumeration, push
            // each artifact's bytes to andy-docs. The client is
            // best-effort: failures collapse to a metadata-only entry
            // (DocsRef stays null) so a transient andy-docs outage
            // never blocks container stop. When the client isn't wired
            // (DI didn't register one — typical in dev / embedded
            // mode without an andy-docs instance), we short-circuit
            // and return the parsed manifest unchanged.
            if (_andyDocs is null || parsed.Count == 0)
            {
                return parsed;
            }

            return await UploadAndAttachDocsRefsAsync(container, parsed, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate the cancel so the terminal
            // path can decide whether to skip the event entirely.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to collect output artifacts from container {ContainerId}: {Message}",
                container.Id, ex.Message);
            return Array.Empty<RunOutputArtifact>();
        }
    }

    // rivoli-ai/andy-containers#320. Per-artifact byte read + andy-docs
    // upload. One bad file MUST NOT kill the whole collection — each
    // file is wrapped in its own try/catch so a single read failure
    // or upload error just leaves that one artifact metadata-only.
    private async Task<IReadOnlyList<RunOutputArtifact>> UploadAndAttachDocsRefsAsync(
        Container container,
        IReadOnlyList<RunOutputArtifact> parsed,
        CancellationToken ct)
    {
        var enriched = new List<RunOutputArtifact>(parsed.Count);
        foreach (var artifact in parsed)
        {
            DocsRef? docsRef = null;
            try
            {
                if (artifact.SizeBytes > MaxUploadSizeBytes)
                {
                    _logger.LogWarning(
                        "Artifact {RelativePath} in container {ContainerId} is {SizeBytes} bytes — exceeds upload cap ({MaxBytes}); emitting metadata-only.",
                        artifact.RelativePath, container.Id, artifact.SizeBytes, MaxUploadSizeBytes);
                }
                else
                {
                    var bytes = await ReadArtifactBytesAsync(container, artifact, ct);
                    if (bytes is not null)
                    {
                        var request = new UploadRequest(
                            Content: bytes.Value,
                            MimeType: artifact.ContentType ?? "application/octet-stream",
                            Name: artifact.Name,
                            Digest: artifact.Sha256,
                            Links: new[]
                            {
                                new DocumentLinkDescriptor(
                                    TargetType: "Run",
                                    TargetId: container.Id.ToString(),
                                    Role: "Output"),
                            });
                        docsRef = await _andyDocs!.UploadAsync(request, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — propagate so the terminal-event
                // path can decide. Don't mask with a log line.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to upload artifact {RelativePath} from container {ContainerId} to andy-docs; emitting metadata-only.",
                    artifact.RelativePath, container.Id);
                docsRef = null;
            }

            // Even if docsRef is null we still surface the artifact —
            // metadata + sha is useful to downstream consumers (#316
            // contract). #320 just adds the optional DocsRef pointer.
            enriched.Add(artifact with { DocsRef = docsRef });
        }
        return enriched;
    }

    // rivoli-ai/andy-containers#320. Read a single artifact's bytes out
    // of the container via the exec channel. We `base64`-encode in the
    // container and decode here — `cat` over an exec channel mangles
    // binary content on every provider that line-buffers stdout (LF/CR
    // translation, EOF detection). base64 is portable across the BSD
    // and GNU coreutils variants we ship in our base images.
    //
    // Returns null on any non-cancellation failure (exec error, decode
    // error, size mismatch). The caller treats null as "no bytes →
    // metadata-only entry, no DocsRef".
    private async Task<ReadOnlyMemory<byte>?> ReadArtifactBytesAsync(
        Container container,
        RunOutputArtifact artifact,
        CancellationToken ct)
    {
        // Re-anchor the relative path against the outputs root. Path
        // separators are normalised to forward slashes by the collector
        // contract, so a naive concat works.
        var absolutePath = OutputsRoot + "/" + artifact.RelativePath;
        // Single-quote the path inside a sh -c invocation, doubling
        // any embedded single quotes via the POSIX `'\''` escape.
        var quoted = "'" + absolutePath.Replace("'", "'\\''") + "'";
        // `-w 0` suppresses line wrapping (BusyBox + GNU coreutils);
        // BSD `base64` ignores `-w` but we tolerate the difference by
        // stripping all whitespace from stdout below before decoding.
        var script = $"base64 -w 0 {quoted} 2>/dev/null || base64 {quoted} 2>/dev/null";

        ExecResult exec;
        try
        {
            exec = await _containers.ExecAsync(
                container.Id, $"sh -c '{script.Replace("'", "'\\''")}'", ReadTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read bytes for artifact {RelativePath} from container {ContainerId}: {Message}",
                artifact.RelativePath, container.Id, ex.Message);
            return null;
        }

        if (exec.ExitCode != 0 || string.IsNullOrWhiteSpace(exec.StdOut))
        {
            _logger.LogWarning(
                "base64 read for artifact {RelativePath} in container {ContainerId} exited {ExitCode}; StdErr: {StdErr}",
                artifact.RelativePath, container.Id, exec.ExitCode, Truncate(exec.StdErr, 200));
            return null;
        }

        try
        {
            // Strip all whitespace (BSD `base64` always wraps at 76;
            // some shells re-emit with \r\n). Pre-strip rather than
            // rely on Convert.FromBase64String's narrow whitespace
            // tolerance.
            var raw = exec.StdOut;
            var buf = new char[raw.Length];
            var n = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (!char.IsWhiteSpace(c)) buf[n++] = c;
            }
            var bytes = Convert.FromBase64CharArray(buf, 0, n);
            return bytes;
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex,
                "base64 output for artifact {RelativePath} in container {ContainerId} failed to decode.",
                artifact.RelativePath, container.Id);
            return null;
        }
    }

    // Parser is internal-visible-for-tests. Each line is
    // `size_bytes \t sha256 \t absolute_path`. Lines that don't match
    // the shape are skipped (defensive — a noisy shell could emit a
    // banner line we don't want to corrupt the manifest with).
    internal static IReadOnlyList<RunOutputArtifact> ParseProbeOutput(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return Array.Empty<RunOutputArtifact>();
        }

        var results = new List<RunOutputArtifact>();
        var rootWithSlash = OutputsRoot.EndsWith('/') ? OutputsRoot : OutputsRoot + "/";

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }

            var sha = parts[1].Trim().ToLowerInvariant();
            if (sha.Length != 64) continue;

            // The path is parts[2..] re-joined (in case the file path
            // itself contained a tab — unusual but POSIX-legal).
            var absolutePath = string.Join('\t', parts.Skip(2));
            string relativePath;
            if (absolutePath.StartsWith(rootWithSlash, StringComparison.Ordinal))
            {
                relativePath = absolutePath[rootWithSlash.Length..];
            }
            else if (absolutePath == OutputsRoot)
            {
                // File is the root itself — shouldn't happen for -type f
                // but tolerate it without crashing.
                continue;
            }
            else
            {
                // Path outside our expected root: skip rather than
                // mis-attribute. Could happen if `find` followed a
                // symlink out of the tree — the artifact contract
                // pins paths relative to the outputs root.
                continue;
            }

            if (relativePath.Length == 0) continue;

            var name = Path.GetFileName(relativePath);
            var contentType = GuessContentType(name);

            results.Add(new RunOutputArtifact(
                Name: name,
                RelativePath: relativePath,
                SizeBytes: size,
                Sha256: sha,
                ContentType: contentType));
        }

        return results;
    }

    // Tiny static MIME map covering the extensions an agent run is
    // most likely to produce. We deliberately don't pull in
    // System.Web.MimeMapping — it brings the full ASP.NET hosting
    // surface as a transitive dep, which is overkill for a 30-entry
    // lookup. Unknown extensions return null and downstream consumers
    // can fall back to application/octet-stream.
    internal static string? GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".yaml" or ".yml" => "application/yaml",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".html" or ".htm" => "text/html",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".tgz" => "application/gzip",
            ".bz2" => "application/x-bzip2",
            ".sh" => "application/x-sh",
            ".py" => "text/x-python",
            ".js" => "application/javascript",
            ".ts" => "application/typescript",
            ".cs" => "text/x-csharp",
            ".go" => "text/x-go",
            ".rs" => "text/x-rust",
            ".sql" => "application/sql",
            _ => null,
        };
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max] + "...";
    }
}

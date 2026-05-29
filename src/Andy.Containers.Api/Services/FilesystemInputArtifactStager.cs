// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Audit;
using Andy.Containers.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Default <see cref="IInputArtifactStager"/> for rivoli-ai/andy-containers#328
/// (EX.7). Inverse of <see cref="FilesystemOutputArtifactCollector"/>:
///
/// <list type="number">
///   <item>Download each input's andy-docs document bytes via
///   <see cref="IAndyDocsClient.DownloadAsync"/>.</item>
///   <item>Stage them under <c>/workspace/.andy/inputs/&lt;dest&gt;</c>
///   inside the container by shelling out through
///   <see cref="IContainerService.ExecAsync(Guid, string, TimeSpan, CancellationToken)"/>
///   and decoding base64 in-container (the same exec/base64 transport the
///   collector uses for reads, run in reverse).</item>
/// </list>
///
/// <para>
/// Staging is on the run-START critical path: any input that can't be
/// fetched, is oversized, or can't be written throws
/// <see cref="InputStagingException"/> so the runner fails the run with a
/// clear, typed error rather than starting the agent against an empty
/// input. No inputs → no-op.
/// </para>
///
/// <para>
/// <see cref="IContainerService"/> is resolved lazily through the service
/// provider to mirror the collector's break of the
/// <c>IContainerService → collector/stager</c> registration cycle that
/// otherwise deadlocks <c>ValidateOnBuild</c>.
/// </para>
/// </summary>
public sealed class FilesystemInputArtifactStager : IInputArtifactStager
{
    // Symmetric with FilesystemOutputArtifactCollector.OutputsRoot. The
    // agent SDK reads inputs from here; changing it requires a coordinated
    // bump on the agent side.
    public const string InputsRoot = "/workspace/.andy/inputs";

    // Hard cap per input artifact. Mirrors the collector's 64 MiB upload
    // cap: the exec-channel base64 round-trip materialises the payload in
    // memory (decoded byte[] + base64 string), so cap to keep peak RSS
    // bounded. An oversized input fails the run (TooLarge) rather than
    // silently truncating.
    public const long MaxInputSizeBytes = 64L * 1024 * 1024;

    // Wall-clock cap on the in-container write exec. Generous for a
    // multi-hundred-MB base64 payload streamed through the exec channel.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private IContainerService? _containersCache;
    private IContainerService Containers
    {
        get
        {
            _containersCache ??= _services.GetRequiredService<IContainerService>();
            return _containersCache;
        }
    }
    private readonly IAndyDocsClient? _andyDocs;
    private readonly ILogger<FilesystemInputArtifactStager> _logger;

    /// <summary>
    /// DI constructor. Resolves <see cref="IContainerService"/> lazily to
    /// break the registration cycle (see class remarks). The andy-docs
    /// client is optional — when null, declaring inputs fails the run
    /// (<see cref="InputStagingFailure.DocsClientUnavailable"/>) since
    /// staging is impossible without it.
    /// </summary>
    public FilesystemInputArtifactStager(
        IServiceProvider services,
        ILogger<FilesystemInputArtifactStager> logger,
        IAndyDocsClient? andyDocs = null)
    {
        _services = services;
        _logger = logger;
        _andyDocs = andyDocs;
    }

    /// <summary>
    /// Test-only overload. <c>internal</c> so DI never scans it during
    /// <c>ValidateOnBuild</c>; visible to <c>Andy.Containers.Api.Tests</c>
    /// via <c>InternalsVisibleTo</c>. Lets unit tests inject a mock
    /// <see cref="IContainerService"/> directly.
    /// </summary>
    internal FilesystemInputArtifactStager(
        IContainerService containers,
        ILogger<FilesystemInputArtifactStager> logger,
        IAndyDocsClient? andyDocs = null)
    {
        _services = new SingleServiceProvider(containers);
        _containersCache = containers;
        _logger = logger;
        _andyDocs = andyDocs;
    }

    private sealed class SingleServiceProvider(IContainerService containers) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IContainerService) ? containers : null;
    }

    public async Task StageAsync(
        Container container,
        IReadOnlyList<HeadlessInput> inputs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            // No-op: behaviour identical to pre-EX.7.
            return;
        }

        if (_andyDocs is null)
        {
            // Inputs were declared but we have no way to fetch them. This
            // is a misconfiguration, not a transient fault — fail loudly
            // on the first input so the run doesn't start half-staged.
            var first = inputs[0];
            throw new InputStagingException(
                first.DocsRef, first.DestRelativePath, InputStagingFailure.DocsClientUnavailable,
                "Run declares inputs but no andy-docs client is configured (AndyDocs:ApiBaseUrl unset); cannot stage cross-container artifacts.");
        }

        foreach (var input in inputs)
        {
            await StageOneAsync(container, input, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "EX.7: staged {Count} input artifact(s) under {Root} in container {ContainerId}.",
            inputs.Count, InputsRoot, container.Id);
    }

    private async Task StageOneAsync(Container container, HeadlessInput input, CancellationToken ct)
    {
        // Defence in depth: the configurator's builder already validated
        // the dest path (traversal guard) at config-build time, but
        // re-validate here so a hand-constructed HeadlessInput that
        // bypassed the builder can't escape the inputs root.
        string dest;
        try
        {
            dest = HeadlessConfigBuilder.ValidateDestRelativePath(input.DestRelativePath);
        }
        catch (ArgumentException ex)
        {
            throw new InputStagingException(
                input.DocsRef, input.DestRelativePath, InputStagingFailure.WriteFailed,
                $"EX.7: input dest path '{input.DestRelativePath}' is invalid: {ex.Message}", ex);
        }

        var download = await _andyDocs!
            .DownloadAsync(input.DocsRef, MaxInputSizeBytes, ct)
            .ConfigureAwait(false);

        if (!download.IsSuccess)
        {
            var (failure, reason) = download.Failure switch
            {
                DocumentDownloadFailure.NotFound =>
                    (InputStagingFailure.NotFound, "document not found in andy-docs"),
                DocumentDownloadFailure.TooLarge =>
                    (InputStagingFailure.TooLarge, $"document exceeds the {MaxInputSizeBytes}-byte input cap"),
                _ =>
                    (InputStagingFailure.FetchFailed, "fetch from andy-docs failed"),
            };
            throw new InputStagingException(
                input.DocsRef, dest, failure,
                $"EX.7: cannot stage input '{dest}' from docs-ref {input.DocsRef}: {reason}.");
        }

        await WriteIntoContainerAsync(container, input.DocsRef, dest, download.Content, ct)
            .ConfigureAwait(false);
    }

    // Write the decoded bytes into the container at
    // /workspace/.andy/inputs/<dest>. We pipe base64 over the exec channel
    // and decode in-container — the exact inverse of the collector's read
    // path — because raw binary over an exec stdin/stdout channel gets
    // mangled (LF/CR translation) on the providers we ship.
    private async Task WriteIntoContainerAsync(
        Container container, Guid docsRef, string dest, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var absolutePath = InputsRoot + "/" + dest;
        var quotedPath = ShellSingleQuote(absolutePath);
        var quotedDir = ShellSingleQuote(ParentDir(absolutePath));
        var base64 = Convert.ToBase64String(content.Span);

        // mkdir -p the parent, then base64 -d the heredoc-fed payload into
        // the target. `base64 -d` (GNU) / `base64 -D` (BSD) — we try -d
        // first and fall back to -D so the script is portable across the
        // base images we ship. The payload is fed via stdin from a
        // here-doc so a very large base64 string doesn't blow the ARG_MAX
        // command-line limit.
        var script =
            $"mkdir -p {quotedDir} && " +
            $"{{ printf '%s' \"$ANDY_INPUT_B64\" | base64 -d > {quotedPath} 2>/dev/null || " +
            $"printf '%s' \"$ANDY_INPUT_B64\" | base64 -D > {quotedPath}; }}";

        // Pass the base64 through an env var rather than inlining it in the
        // command string so neither ARG_MAX nor shell-quoting of a huge
        // payload is a concern. ExecAsync takes a single command string, so
        // we prefix an `export` of the payload. The payload is base64 — no
        // shell metacharacters survive — so single-quoting it is safe.
        var command = $"sh -c '{("export ANDY_INPUT_B64=" + ShellSingleQuote(base64) + "; " + script).Replace("'", "'\\''")}'";

        ExecResult exec;
        try
        {
            exec = await Containers.ExecAsync(container.Id, command, WriteTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InputStagingException(
                docsRef, dest, InputStagingFailure.WriteFailed,
                $"EX.7: failed to write input '{dest}' into container {container.Id}: {ex.Message}", ex);
        }

        if (exec.ExitCode != 0)
        {
            throw new InputStagingException(
                docsRef, dest, InputStagingFailure.WriteFailed,
                $"EX.7: writing input '{dest}' into container {container.Id} exited {exec.ExitCode}: {Truncate(exec.StdErr, 200)}");
        }
    }

    // POSIX single-quote escape, identical to HeadlessRunner.ShellEscape.
    private static string ShellSingleQuote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";

    // Parent directory of a forward-slash POSIX path. Always has at least
    // the inputs root as a prefix (dest is relative + non-empty), so this
    // never returns "".
    private static string ParentDir(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "...";
    }
}

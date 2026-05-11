using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#945 (M1.5.3). Default
/// <see cref="ICodeAssistantInstallExecutor"/>: generate script via
/// <see cref="ICodeAssistantInstallService"/>, exec via
/// <see cref="IContainerService"/>, capture outcome on the
/// <see cref="Container"/> row.
/// </summary>
public sealed class CodeAssistantInstallExecutor : ICodeAssistantInstallExecutor
{
    /// <summary>
    /// Time budget for the install script. The npm/cargo/pip
    /// installers some assistants drag in can be slow on first run
    /// (no cache, dial-up corp networks); 10 minutes covers all
    /// pre-bake-less variants we ship today.
    /// </summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cap on the captured stderr / exception summary persisted in
    /// <see cref="Container.CodeAssistantStatusReason"/>. The UI
    /// renders this in a banner; gigabytes of npm output would blow
    /// the row up and offer the user no actionable signal anyway.
    /// </summary>
    public const int StatusReasonMaxLength = 500;

    private readonly ICodeAssistantInstallService _installService;
    private readonly IContainerService _containerService;
    private readonly TimeProvider _time;
    private readonly ILogger<CodeAssistantInstallExecutor> _logger;

    public CodeAssistantInstallExecutor(
        ICodeAssistantInstallService installService,
        IContainerService containerService,
        ILogger<CodeAssistantInstallExecutor> logger,
        TimeProvider? time = null)
    {
        _installService = installService;
        _containerService = containerService;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task RunAsync(Container container, CodeAssistantConfig codeAssistant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(codeAssistant);

        // Mark Installing immediately. If the caller persists the row
        // before the script returns, the UI sees the in-progress state.
        container.CodeAssistantStatus = CodeAssistantInstallStatus.Installing;
        container.CodeAssistantStatusAt = _time.GetUtcNow().UtcDateTime;
        container.CodeAssistantStatusReason = null;

        string? installScript;
        try
        {
            installScript = _installService.GenerateInstallScript(codeAssistant);
        }
        catch (Exception genEx)
        {
            container.CodeAssistantStatus = CodeAssistantInstallStatus.Skipped;
            container.CodeAssistantStatusReason =
                $"script-generation: {genEx.GetType().Name}: {SummariseReason(genEx.Message)}";
            container.CodeAssistantStatusAt = _time.GetUtcNow().UtcDateTime;
            _logger.LogWarning(genEx,
                "Code assistant install skipped for container {ContainerId}: script generation failed.",
                container.Id);
            return;
        }

        try
        {
            _logger.LogInformation(
                "Installing code assistant {Tool} for container {ContainerId}",
                codeAssistant.Tool, container.Id);
            var installResult = await _containerService
                .ExecAsync(container.Id, installScript, InstallTimeout, ct)
                .ConfigureAwait(false);
            container.CodeAssistantStatusAt = _time.GetUtcNow().UtcDateTime;
            if (installResult.ExitCode != 0)
            {
                container.CodeAssistantStatus = CodeAssistantInstallStatus.Failed;
                container.CodeAssistantStatusReason =
                    $"exit-code-{installResult.ExitCode}: {SummariseReason(installResult.StdErr)}";
                _logger.LogWarning(
                    "Code assistant install exited with {ExitCode} for container {ContainerId}: {StdErr}",
                    installResult.ExitCode, container.Id, installResult.StdErr);
            }
            else
            {
                container.CodeAssistantStatus = CodeAssistantInstallStatus.Installed;
                container.CodeAssistantStatusReason = null;
                _logger.LogInformation(
                    "Code assistant {Tool} installed for container {ContainerId}",
                    codeAssistant.Tool, container.Id);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Local timeout from ExecAsync's internal CancelAfter —
            // not a global shutdown. Treat as install timeout.
            container.CodeAssistantStatus = CodeAssistantInstallStatus.Failed;
            container.CodeAssistantStatusReason =
                $"timeout: install script exceeded the {InstallTimeout.TotalMinutes:F0}-minute budget";
            container.CodeAssistantStatusAt = _time.GetUtcNow().UtcDateTime;
            _logger.LogWarning(
                "Code assistant install timed out for container {ContainerId}",
                container.Id);
        }
        catch (Exception ex)
        {
            container.CodeAssistantStatus = CodeAssistantInstallStatus.Failed;
            container.CodeAssistantStatusReason =
                $"exception: {ex.GetType().Name}: {SummariseReason(ex.Message)}";
            container.CodeAssistantStatusAt = _time.GetUtcNow().UtcDateTime;
            _logger.LogWarning(ex,
                "Code assistant install failed for container {ContainerId}",
                container.Id);
        }
    }

    /// <summary>
    /// Compress a multi-line stderr / exception message into a
    /// single-line UI-friendly summary that fits in the Container
    /// row's status reason column.
    /// </summary>
    public static string SummariseReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "<no detail>";
        var lines = raw.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(" | ", lines);
        return joined.Length <= StatusReasonMaxLength
            ? joined
            : joined[..(StatusReasonMaxLength - 1)] + "…";
    }
}

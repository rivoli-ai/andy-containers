using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Registries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Maps every image management failure mode to a consistently-shaped
/// HTTP response — the IM5 OpenAPI <c>ImageManagementError</c>
/// schema with stable <see cref="ImageManagementErrors"/> codes,
/// human-readable messages, optional field paths, and (for build
/// failures) truncated build logs.
/// </summary>
/// <remarks>
/// IM10 (rivoli-ai/andy-containers#264). One factory method per
/// failure-source so callers don't reinvent the mapping. The build-
/// log truncation cap is enforced here at the response boundary;
/// the full log stays accessible via
/// <c>GET /api/images/build/{buildId}</c>.
/// </remarks>
public static class ImageManagementProblemDetailsFactory
{
    /// <summary>
    /// Maximum size of <c>buildLog</c> in 4xx/5xx response bodies.
    /// 64 KiB matches the OpenAPI documentation; the full log is
    /// available via the build status snapshot.
    /// </summary>
    public const int MaxBuildLogBytes = 64 * 1024;

    /// <summary>
    /// Map an <see cref="ImageBuildFailedException"/> from the build
    /// backend onto a 422 response with captured logs.
    /// </summary>
    public static ObjectResult FromBuildFailure(ImageBuildFailedException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var code = ex.FailingStepName switch
        {
            "engine-detect" => ImageManagementErrors.BuildEngineUnavailable,
            _ => ImageManagementErrors.BuildFailed,
        };
        var status = code == ImageManagementErrors.BuildEngineUnavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status422UnprocessableEntity;
        return Build(status, code, ex.Message, buildLog: ex.CapturedLogs);
    }

    /// <summary>
    /// Map a <see cref="RegistryUploadException"/> onto an
    /// appropriate HTTP response. Most upload failures are 502/503
    /// territory; quota exhaustion is 507; auth issues are 401/403
    /// but those are handled upstream by middleware.
    /// </summary>
    public static ObjectResult FromRegistryFailure(RegistryUploadException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var (status, code) = ex.Code switch
        {
            var c when c.StartsWith("DockerCliUploader") && c.EndsWith("LaunchFailed") =>
                (StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.BuildEngineUnavailable),
            _ when ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status507InsufficientStorage, ImageManagementErrors.RegistryQuotaExceeded),
            _ => (StatusCodes.Status422UnprocessableEntity, ImageManagementErrors.BuildFailed),
        };
        return Build(status, code, ex.Message, buildLog: ex.CapturedOutput);
    }

    /// <summary>
    /// Map an orchestrator-level error code (the string carried on
    /// <c>BuildResult.ErrorCode</c>) onto the response shape. Used
    /// when the orchestrator's failure is observed via the registry's
    /// terminal state rather than a thrown exception.
    /// </summary>
    public static ObjectResult FromOrchestratorErrorCode(
        string? errorCode,
        string? errorMessage,
        string? failureLog)
    {
        var (status, code) = errorCode switch
        {
            ImageManagementErrors.TemplateNotFound => (StatusCodes.Status404NotFound, ImageManagementErrors.TemplateNotFound),
            ImageManagementErrors.RegistryNotConfigured => (StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.RegistryNotConfigured),
            null => (StatusCodes.Status422UnprocessableEntity, ImageManagementErrors.BuildFailed),
            _ when errorCode.StartsWith("build.engine") => (StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.BuildEngineUnavailable),
            _ when errorCode.StartsWith("registry.quota") => (StatusCodes.Status507InsufficientStorage, ImageManagementErrors.RegistryQuotaExceeded),
            _ when errorCode.StartsWith("registry.not_configured") => (StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.RegistryNotConfigured),
            _ when errorCode.StartsWith("template.not_found") => (StatusCodes.Status404NotFound, ImageManagementErrors.TemplateNotFound),
            _ => (StatusCodes.Status422UnprocessableEntity, ImageManagementErrors.BuildFailed),
        };
        return Build(status, code, errorMessage ?? "build failed", buildLog: failureLog);
    }

    /// <summary>
    /// 400: malformed YAML or schema-validation failure. The
    /// validation result's per-field errors are flattened into the
    /// response so the caller knows where to look.
    /// </summary>
    public static ObjectResult FromValidationErrors(YamlValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var first = validation.Errors.FirstOrDefault();
        return Build(
            StatusCodes.Status400BadRequest,
            ImageManagementErrors.TemplateSpecInvalid,
            first?.Message ?? "spec failed validation",
            field: first?.Field,
            extras: new
            {
                errors = validation.Errors.Select(e => new { field = e.Field, message = e.Message, line = e.Line }).ToList(),
                warnings = validation.Warnings.Select(w => new { field = w.Field, message = w.Message, line = w.Line }).ToList(),
            });
    }

    /// <summary>
    /// 409: re-registering a template code with a different spec
    /// hash. Carries the existing template's id so the caller can
    /// resolve the conflict via PUT-based update if appropriate.
    /// </summary>
    public static ObjectResult FromCodeInUse(string code, Guid existingTemplateId)
    {
        return Build(
            StatusCodes.Status409Conflict,
            ImageManagementErrors.TemplateCodeInUse,
            $"template '{code}' is already registered with a different specHash. " +
            "Bump the template version or update via PUT /templates/{id}/definition.",
            field: "code",
            extras: new { existingTemplateId });
    }

    /// <summary>
    /// 404 helper for any "resource not found" path that doesn't
    /// already have a code at the call site.
    /// </summary>
    public static ObjectResult NotFound(string code, string message)
        => Build(StatusCodes.Status404NotFound, code, message);

    /// <summary>
    /// Build the actual response object. Centralised so
    /// <c>buildLog</c> truncation, JSON property casing, and the
    /// shape are consistent.
    /// </summary>
    private static ObjectResult Build(
        int statusCode,
        string code,
        string message,
        string? field = null,
        string? buildLog = null,
        object? extras = null)
    {
        var truncated = TruncateLog(buildLog, MaxBuildLogBytes);
        // Use a property-named anonymous object — JSON serialisation
        // preserves the shape; ImageManagementError consumers can
        // model it as a strict record.
        var body = new ImageManagementErrorBody
        {
            Code = code,
            Message = message,
            Field = field,
            BuildLog = truncated,
            Extras = extras,
        };
        return new ObjectResult(body) { StatusCode = statusCode };
    }

    /// <summary>
    /// Truncate a build log to a UTF-8 byte cap without splitting a
    /// codepoint. The truncation marker is appended on overflow so a
    /// caller can tell the log was cut.
    /// </summary>
    public static string? TruncateLog(string? log, int maxBytes)
    {
        if (string.IsNullOrEmpty(log))
        {
            return log;
        }
        var bytes = System.Text.Encoding.UTF8.GetByteCount(log);
        if (bytes <= maxBytes)
        {
            return log;
        }

        // Walk down character-by-character until we fit. Conservative
        // — UTF-8 codepoints are at most 4 bytes, so this terminates
        // quickly. Splitting at a codepoint boundary keeps the
        // resulting string valid UTF-8.
        const string suffix = "\n…[truncated]";
        var suffixBytes = System.Text.Encoding.UTF8.GetByteCount(suffix);
        var budget = maxBytes - suffixBytes;
        if (budget <= 0)
        {
            return suffix;
        }

        var truncatedLength = log.Length;
        while (truncatedLength > 0 &&
               System.Text.Encoding.UTF8.GetByteCount(log.AsSpan(0, truncatedLength)) > budget)
        {
            truncatedLength--;
        }
        return log[..truncatedLength] + suffix;
    }
}

/// <summary>
/// Wire-format body for an <c>ImageManagementError</c> response.
/// Properties are PascalCase here; ASP.NET's default
/// <see cref="System.Text.Json.JsonSerializerOptions"/> handles the
/// camelCase output.
/// </summary>
public sealed class ImageManagementErrorBody
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Field { get; init; }
    public string? BuildLog { get; init; }
    public object? Extras { get; init; }
}

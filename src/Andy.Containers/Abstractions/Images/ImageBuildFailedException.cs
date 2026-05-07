namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Thrown by an <see cref="IBuildBackend"/> when the underlying engine
/// reports a non-zero exit (or any other non-recoverable failure).
/// The exception carries captured logs so the API layer can surface
/// them as a structured 422 response per IM10.
/// </summary>
public sealed class ImageBuildFailedException : Exception
{
    /// <summary>
    /// The build backend's id at the time of failure.
    /// </summary>
    public string BackendId { get; }

    /// <summary>
    /// The spec hash that was being built. Populated when known.
    /// </summary>
    public string? SpecHash { get; }

    /// <summary>
    /// The failing step's name, if the failure can be attributed to a
    /// specific step.
    /// </summary>
    public string? FailingStepName { get; }

    /// <summary>
    /// Captured stdout/stderr from the build engine, intended for
    /// inclusion in the API's error response.
    /// </summary>
    public string CapturedLogs { get; }

    public ImageBuildFailedException(
        string backendId,
        string capturedLogs,
        string? specHash = null,
        string? failingStepName = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? BuildDefaultMessage(backendId, failingStepName), innerException)
    {
        BackendId = backendId;
        SpecHash = specHash;
        FailingStepName = failingStepName;
        CapturedLogs = capturedLogs;
    }

    private static string BuildDefaultMessage(string backendId, string? failingStepName)
        => failingStepName is null
            ? $"Build failed on backend '{backendId}'."
            : $"Build failed on backend '{backendId}' at step '{failingStepName}'.";
}

namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Inputs to a build — the directory the build engine operates against
/// plus metadata about uploaded files referenced by the spec's
/// <c>files:</c> entries.
/// </summary>
public interface IBuildContext
{
    /// <summary>
    /// Absolute path on the host where the build context has been
    /// staged. Backends pass this to the build engine as the build
    /// context root.
    /// </summary>
    string ContextDirectoryPath { get; }

    /// <summary>
    /// Files uploaded with the template registration request, indexed
    /// by their <c>files[name]</c> multipart name. Backends use this to
    /// resolve <c>files:</c> entries in the spec.
    /// </summary>
    IReadOnlyList<UploadedFile> Files { get; }
}

/// <summary>
/// One file uploaded alongside a template spec.
/// </summary>
/// <param name="LogicalName">
/// The <c>files[<em>name</em>]</c> part name in the multipart request.
/// </param>
/// <param name="AbsolutePath">
/// Absolute path on disk where the file is staged (within
/// <see cref="IBuildContext.ContextDirectoryPath"/>).
/// </param>
/// <param name="SizeBytes">File size on disk.</param>
public sealed record UploadedFile(
    string LogicalName,
    string AbsolutePath,
    long SizeBytes);

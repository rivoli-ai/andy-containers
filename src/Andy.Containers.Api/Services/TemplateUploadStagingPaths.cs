namespace Andy.Containers.Api.Services;

/// <summary>
/// #277. Single source of truth for the on-disk location of
/// multipart-staged template files. Shared by the multipart register
/// path (<c>TemplatesController.CreateFromYamlMultipart</c>, which
/// creates the dirs) and the TTL sweeper
/// (<see cref="TemplateUploadStagingCleanupWorker"/>, PR C, which
/// reclaims them).
/// </summary>
public static class TemplateUploadStagingPaths
{
    /// <summary>
    /// Absolute path to the staging root —
    /// <c>&lt;temp&gt;/andy-containers/template-uploads/staging</c>.
    /// Each multipart register call creates one <c>&lt;stagingId&gt;</c>
    /// subdirectory underneath; that subdirectory's full path lands in
    /// <c>ContainerTemplate.UploadedFilesPath</c>.
    /// </summary>
    public static string GetStagingRoot()
        => Path.Combine(
            Path.GetTempPath(),
            "andy-containers",
            "template-uploads",
            "staging");
}

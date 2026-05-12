namespace Andy.Containers.Api.Services;

/// <summary>
/// #277 PR C. Options for
/// <see cref="TemplateUploadStagingCleanupWorker"/>. Bound from the
/// <c>Containers:Image:TemplateUploadStaging</c> config section.
/// </summary>
public sealed class TemplateUploadStagingCleanupOptions
{
    public const string SectionName = "Containers:Image:TemplateUploadStaging";

    /// <summary>
    /// How often the sweeper checks for stale staging dirs. Default
    /// 1 hour — staging dirs are write-once and the cost of scanning
    /// is bounded by the directory count.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Minimum age before an unreferenced staging dir is eligible for
    /// deletion. Default 7 days — long enough that an operator who
    /// uploaded files and intends to come back to them inside the
    /// week doesn't lose their staged content, short enough that
    /// abandoned uploads from misbehaving clients can't pile up
    /// indefinitely.
    /// </summary>
    /// <remarks>
    /// Dirs still referenced by some <c>Template.UploadedFilesPath</c>
    /// row are NEVER deleted regardless of age, so force-rebuilds of
    /// long-lived templates always have their source files. Only
    /// orphaned dirs are subject to the retention cutoff.
    /// </remarks>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

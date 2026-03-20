namespace Andy.Containers.Web.Shared;

public class DependencyItem
{
    public string Type { get; set; } = "tool";
    public string Name { get; set; } = "";
    public string VersionConstraint { get; set; } = "";
    public string? ResolvedVersion { get; set; }
    public bool AutoUpdate { get; set; }
    public string? UpdatePolicy { get; set; }
    public DependencyStatus Status { get; set; } = DependencyStatus.NotResolved;
}

public enum DependencyStatus
{
    NotResolved,
    Resolved,
    UpdateAvailable,
    ConstraintViolation
}

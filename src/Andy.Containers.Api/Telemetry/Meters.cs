using System.Diagnostics.Metrics;

namespace Andy.Containers.Api.Telemetry;

public static class Meters
{
    private static readonly Meter ContainerMeter = new("Andy.Containers");

    // Counters
    public static readonly Counter<long> ContainersCreated = ContainerMeter.CreateCounter<long>(
        "andy.containers.created", "containers", "Number of containers created");
    public static readonly Counter<long> ContainersDeleted = ContainerMeter.CreateCounter<long>(
        "andy.containers.deleted", "containers", "Number of containers deleted");
    public static readonly Counter<long> GitClonesCompleted = ContainerMeter.CreateCounter<long>(
        "andy.containers.git.clones_completed", "clones", "Number of git clones completed");
    public static readonly Counter<long> GitClonesFailed = ContainerMeter.CreateCounter<long>(
        "andy.containers.git.clones_failed", "clones", "Number of git clones that failed");
    public static readonly Counter<long> ProvisioningErrors = ContainerMeter.CreateCounter<long>(
        "andy.containers.provisioning.errors", "errors", "Number of provisioning errors");
    public static readonly Counter<long> HealthChecksCompleted = ContainerMeter.CreateCounter<long>(
        "andy.containers.health_checks.completed", "checks", "Number of provider health checks completed");
    public static readonly Counter<long> HealthCheckErrors = ContainerMeter.CreateCounter<long>(
        "andy.containers.health_checks.errors", "errors", "Number of provider health check errors");

    // Histograms
    public static readonly Histogram<double> ProvisioningDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.provisioning.duration", "ms", "Time to provision a container");
    public static readonly Histogram<double> GitCloneDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.git.clone_duration", "ms", "Time to clone a git repository");
    public static readonly Histogram<double> ImageDiffDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.image.diff_duration", "ms", "Time to compute image diff");

    public static readonly string[] All = [ContainerMeter.Name];
}

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
    public static readonly Counter<long> ApiKeysCreated = ContainerMeter.CreateCounter<long>(
        "andy.containers.api_keys.created", "keys", "Number of API keys created");
    public static readonly Counter<long> ApiKeysValidated = ContainerMeter.CreateCounter<long>(
        "andy.containers.api_keys.validated", "validations", "Number of API key validations");
    public static readonly Counter<long> ApiKeysInjected = ContainerMeter.CreateCounter<long>(
        "andy.containers.api_keys.injected", "injections", "Number of API key injections into containers");
    public static readonly Counter<long> ApiKeysDeleted = ContainerMeter.CreateCounter<long>(
        "andy.containers.api_keys.deleted", "keys", "Number of API keys deleted");

    // Histograms
    public static readonly Histogram<double> ProvisioningDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.provisioning.duration", "ms", "Time to provision a container");
    public static readonly Histogram<double> GitCloneDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.git.clone_duration", "ms", "Time to clone a git repository");
    public static readonly Histogram<double> ImageDiffDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.image.diff_duration", "ms", "Time to compute image diff");
    public static readonly Histogram<double> ApiKeyValidationDuration = ContainerMeter.CreateHistogram<double>(
        "andy.containers.api_keys.validation_duration", "ms", "Time to validate an API key");

    public static readonly string[] All = [ContainerMeter.Name];
}

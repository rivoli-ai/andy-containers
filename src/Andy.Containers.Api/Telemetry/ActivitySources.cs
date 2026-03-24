using System.Diagnostics;

namespace Andy.Containers.Api.Telemetry;

public static class ActivitySources
{
    public static readonly ActivitySource Provisioning = new("Andy.Containers.Provisioning");
    public static readonly ActivitySource Introspection = new("Andy.Containers.Introspection");
    public static readonly ActivitySource Git = new("Andy.Containers.Git");
    public static readonly ActivitySource Infrastructure = new("Andy.Containers.Infrastructure");
    public static readonly ActivitySource ApiKeys = new("Andy.Containers.ApiKeys");

    public static readonly string[] All =
    [
        Provisioning.Name,
        Introspection.Name,
        Git.Name,
        Infrastructure.Name,
        ApiKeys.Name
    ];
}

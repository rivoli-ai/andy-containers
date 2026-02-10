namespace Andy.Containers.Configuration;

public class ContainersOptions
{
    public const string SectionName = "Containers";

    public string ApiBaseUrl { get; set; } = "https://localhost:5200";
    public string? GrpcEndpoint { get; set; }
    public CacheOptions Cache { get; set; } = new();
    public CleanupOptions Cleanup { get; set; } = new();
    public BuildOptions Build { get; set; } = new();
}

public class CacheOptions
{
    public int DefaultExpirationMinutes { get; set; } = 5;
    public bool UseDistributedCache { get; set; }
}

public class CleanupOptions
{
    /// <summary>How often to check for expired containers (minutes).</summary>
    public int CheckIntervalMinutes { get; set; } = 5;

    /// <summary>Auto-stop containers with no active sessions after this duration (minutes).</summary>
    public int IdleTimeoutMinutes { get; set; } = 120;

    /// <summary>Auto-destroy stopped containers after this duration (hours).</summary>
    public int StoppedRetentionHours { get; set; } = 48;
}

public class BuildOptions
{
    /// <summary>How often to check for upstream dependency updates (hours).</summary>
    public int UpdateCheckIntervalHours { get; set; } = 6;

    /// <summary>Directory for offline dependency cache.</summary>
    public string? OfflineCachePath { get; set; }

    /// <summary>Default to offline builds when cache is available.</summary>
    public bool PreferOffline { get; set; }
}

namespace Andy.Containers.Validation;

/// <summary>
/// Central definition of the image references that are BUILT LOCALLY by the
/// Docker provider from the repo's own <c>images/&lt;name&gt;/Dockerfile</c>
/// fixtures rather than pulled from a registry: the <c>andy-desktop-*</c>
/// family and (rivoli-ai/andy-tasks#390) the pre-baked agent-run image
/// <c>andy-agent-cli:latest</c>.
///
/// Both the digest-pin exemption in <c>ContainerOrchestrationService</c> and
/// the build-on-miss trigger in <c>DockerInfrastructureProvider</c> key on
/// this class so the two call sites can never disagree about what counts as
/// a locally-built image.
/// </summary>
public static class LocalImages
{
    /// <summary>
    /// The pre-baked agent-run image (rivoli-ai/andy-tasks#390): ubuntu:24.04
    /// + base tooling + .NET 8 SDK + a published andy-cli on PATH. Built once
    /// from <c>images/agent-cli/Dockerfile</c>; containers provisioned from it
    /// reach Ready in seconds instead of paying a &gt;5-minute in-container
    /// <c>dotnet publish</c>.
    /// </summary>
    public const string AgentCli = "andy-agent-cli:latest";

    /// <summary>
    /// The base image the <c>andy-cli-dev</c> template used before #390 and
    /// the fallback non-Docker providers provision from (their post_create
    /// script source-builds andy-cli, the pre-#390 behaviour).
    /// </summary>
    public const string AgentCliFallbackBase = "ubuntu:24.04";

    /// <summary>True when the reference names the pre-baked agent-run image.</summary>
    public static bool IsAgentCli(string imageReference) =>
        string.Equals(imageReference, AgentCli, StringComparison.Ordinal)
        || string.Equals(imageReference, "andy-agent-cli", StringComparison.Ordinal);

    /// <summary>
    /// True when the reference is a locally-built fixture image (never pulled
    /// from a registry): <c>andy-desktop-*</c> or the agent-run image.
    /// </summary>
    public static bool IsLocallyBuilt(string imageReference) =>
        imageReference.StartsWith("andy-desktop-", StringComparison.Ordinal)
        || imageReference.StartsWith("andy-devpilot-", StringComparison.Ordinal)
        || IsAgentCli(imageReference);
}

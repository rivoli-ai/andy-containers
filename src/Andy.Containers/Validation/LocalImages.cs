namespace Andy.Containers.Validation;

/// <summary>
/// Central definition of the image references that are BUILT LOCALLY by the
/// Docker provider from the repo's own <c>images/&lt;name&gt;/Dockerfile</c>
/// fixtures rather than pulled from a registry: the <c>andy-desktop-*</c>
/// family and (rivoli-ai/andy-tasks#390) the pre-baked agent-run image
/// revision-tagged <c>andy-agent-cli</c> image.
///
/// Both the digest-pin exemption in <c>ContainerOrchestrationService</c> and
/// the build-on-miss trigger in <c>DockerInfrastructureProvider</c> key on
/// this class so the two call sites can never disagree about what counts as
/// a locally-built image.
/// </summary>
public static class LocalImages
{
    /// <summary>
    /// Immutable andy-cli source revision baked into the pre-baked agent image.
    /// The image includes ubuntu:24.04, base tooling, the .NET 8 SDK, and a
    /// published andy-cli on PATH. Updating
    /// the CLI is an explicit source change: bump this full commit and the
    /// short tag below together. That gives every host running a given
    /// andy-containers revision identical CLI bytes and naturally invalidates
    /// an older local image without requiring <c>docker rmi</c>.
    /// </summary>
    public const string AgentCliGitRevision = "3f08f5bb340ea9a7e09c80ab6cc31066ec577f4b";

    /// <summary>Revision-tagged local image selected by seeded templates.</summary>
    public const string AgentCli = "andy-agent-cli:3f08f5bb340e";

    /// <summary>
    /// The base image the <c>andy-cli-dev</c> template used before #390 and
    /// the fallback non-Docker providers provision from (their post_create
    /// script source-builds andy-cli, the pre-#390 behaviour).
    /// </summary>
    public const string AgentCliFallbackBase = "ubuntu:24.04";

    /// <summary>True when the reference names the pre-baked agent-run image.</summary>
    public static bool IsAgentCli(string imageReference) =>
        string.Equals(imageReference, AgentCli, StringComparison.Ordinal)
        // Recognise the pre-revision-tag forms so existing configuration can
        // still take the non-Docker fallback and local build-context path.
        || string.Equals(imageReference, "andy-agent-cli:latest", StringComparison.Ordinal)
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

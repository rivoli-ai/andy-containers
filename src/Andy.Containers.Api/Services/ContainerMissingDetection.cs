using Docker.DotNet;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#2204. Detects "the backing container no longer
/// exists on the provider" failures across infrastructure providers.
/// The local Docker provider surfaces a typed
/// <see cref="DockerContainerNotFoundException"/>; other providers
/// (e.g. AWS Fargate) throw <see cref="InvalidOperationException"/>
/// with a "not found" / "no such" message. Both mean the same thing:
/// the DB record points at a container that was deleted out-of-band
/// (docker prune, host reboot, manual rm).
/// </summary>
internal static class ContainerMissingDetection
{
    internal static bool IsContainerMissing(Exception ex) =>
        ex is DockerContainerNotFoundException ||
        (ex is InvalidOperationException &&
         (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains("no such", StringComparison.OrdinalIgnoreCase)));
}

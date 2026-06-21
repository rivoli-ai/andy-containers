namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// Turns a raw <c>docker push</c> failure into an actionable, loud
/// message when the failure signature matches a known Docker Desktop
/// misconfiguration — the loopback-unreachable timeout or the
/// HTTP-vs-HTTPS / insecure-registry rejection.
/// </summary>
/// <remarks>
/// The user has been explicit: NO silent push failures. When we've
/// rewritten the push target to <c>host.docker.internal:5050</c> and
/// the daemon still can't reach it (because zot binds loopback-only)
/// or rejects it (because the host isn't in <c>insecure-registries</c>),
/// the raw Go networking error is useless to a human. This appends the
/// exact registry address and the exact <c>insecure-registries</c>
/// entry to add.
/// </remarks>
public static class RegistryPushFailureDiagnostics
{
    /// <summary>
    /// Build a hint explaining a Docker Desktop push failure, or null
    /// when the failure doesn't match a known signature.
    /// </summary>
    /// <param name="targetAuthority">
    /// The authority the push targeted, e.g.
    /// <c>host.docker.internal:5050</c>.
    /// </param>
    /// <param name="dockerOutput">
    /// Combined stdout/stderr from the failed <c>docker push</c>.
    /// </param>
    /// <param name="wasRewritten">
    /// Whether the target was rewritten to the Docker Desktop alias —
    /// only then do the Docker Desktop preconditions apply.
    /// </param>
    public static string? BuildHint(string targetAuthority, string? dockerOutput, bool wasRewritten)
    {
        var output = dockerOutput ?? string.Empty;

        // HTTP-vs-HTTPS / insecure-registry rejection. Docker treats a
        // non-localhost registry as HTTPS by default; pushing plain HTTP
        // surfaces one of these signatures.
        if (LooksLikeTlsRejection(output))
        {
            return
                $"The registry '{targetAuthority}' is served over plain HTTP, but the Docker daemon " +
                $"requires HTTPS for any registry that isn't 'localhost'. Add '{targetAuthority}' to the " +
                "Docker daemon's insecure-registries and restart Docker Desktop:\n" +
                "  Docker Desktop → Settings → Docker Engine, add:\n" +
                "    \"insecure-registries\": [\"" + targetAuthority + "\"]\n" +
                "then click Apply & Restart.";
        }

        // Loopback-unreachable timeout. Either zot binds loopback-only
        // (so the VM can't reach host.docker.internal) or the host alias
        // didn't resolve.
        if (LooksLikeConnectionTimeout(output))
        {
            if (wasRewritten)
            {
                return
                    $"The Docker daemon could not reach the registry at '{targetAuthority}'. On Docker " +
                    "Desktop the daemon runs in a VM, so the embedded registry must bind a VM-reachable " +
                    "interface (e.g. 0.0.0.0:5050, not 127.0.0.1:5050) and be in insecure-registries. " +
                    $"Confirm zot is bound to 0.0.0.0 and add '{targetAuthority}' to Docker Desktop's " +
                    "insecure-registries (Settings → Docker Engine), then Apply & Restart.";
            }

            return
                $"The Docker daemon could not reach the registry at '{targetAuthority}'. On Docker Desktop " +
                "the daemon runs in a VM where 'localhost' is the VM, not the host — it cannot reach a " +
                "host-loopback registry. The push target must be a VM-reachable address such as " +
                $"'{PushTargetHostResolver.DockerDesktopHostAlias}:<port>', the registry must bind a " +
                "VM-reachable interface (0.0.0.0), and that address must be in Docker Desktop's " +
                "insecure-registries.";
        }

        return null;
    }

    private static bool LooksLikeConnectionTimeout(string output)
        => Contains(output, "Client.Timeout exceeded while awaiting headers")
        || Contains(output, "request canceled while waiting for connection")
        || Contains(output, "connection refused")
        || Contains(output, "no route to host")
        || Contains(output, "i/o timeout")
        || Contains(output, "dial tcp");

    private static bool LooksLikeTlsRejection(string output)
        => Contains(output, "http: server gave HTTP response to HTTPS client")
        || Contains(output, "tls: first record does not look like a TLS handshake")
        || (Contains(output, "x509") && Contains(output, "certificate"))
        || Contains(output, "server gave HTTP response to HTTPS client");

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

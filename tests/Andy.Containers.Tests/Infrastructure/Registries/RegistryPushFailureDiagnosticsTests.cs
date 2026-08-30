using Andy.Containers.Infrastructure.Registries.Local;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Registries;

// NO silent push failures. When a docker push to a rewritten
// host.docker.internal target fails with a loopback-timeout or an
// HTTP-vs-HTTPS rejection, the bare Go networking error is useless to
// a human. These tests lock down the actionable hint that names the
// exact registry address and the insecure-registries entry to add.
public class RegistryPushFailureDiagnosticsTests
{
    [Fact]
    public void BuildHint_TlsRejection_ExplainsInsecureRegistries()
    {
        const string output =
            "http: server gave HTTP response to HTTPS client";

        var hint = RegistryPushFailureDiagnostics.BuildHint(
            "host.docker.internal:5050", output, wasRewritten: true);

        hint.Should().NotBeNull();
        hint.Should().Contain("insecure-registries");
        hint.Should().Contain("host.docker.internal:5050");
        hint.Should().Contain("Apply & Restart");
    }

    [Fact]
    public void BuildHint_ConnectionTimeout_AfterRewrite_MentionsBindAndInsecure()
    {
        const string output =
            "Get \"http://host.docker.internal:5050/v2/\": net/http: request canceled " +
            "while waiting for connection (Client.Timeout exceeded while awaiting headers)";

        var hint = RegistryPushFailureDiagnostics.BuildHint(
            "host.docker.internal:5050", output, wasRewritten: true);

        hint.Should().NotBeNull();
        hint.Should().Contain("host.docker.internal:5050");
        hint.Should().Contain("0.0.0.0", "the registry must bind a VM-reachable interface");
        hint.Should().Contain("insecure-registries");
    }

    [Fact]
    public void BuildHint_ConnectionTimeout_WithoutRewrite_ExplainsLoopbackGap()
    {
        // The original failing case: target still localhost, push from
        // the VM times out.
        const string output =
            "Get \"http://localhost:5050/v2/\": net/http: request canceled while " +
            "waiting for connection (Client.Timeout exceeded while awaiting headers)";

        var hint = RegistryPushFailureDiagnostics.BuildHint(
            "localhost:5050", output, wasRewritten: false);

        hint.Should().NotBeNull();
        hint.Should().Contain("host.docker.internal");
        hint.Should().Contain("VM");
    }

    [Fact]
    public void BuildHint_UnrelatedFailure_ReturnsNull()
    {
        const string output = "denied: requested access to the resource is denied";

        var hint = RegistryPushFailureDiagnostics.BuildHint(
            "host.docker.internal:5050", output, wasRewritten: true);

        hint.Should().BeNull("an auth-denied error is not a Docker Desktop misconfig");
    }

    [Fact]
    public void BuildHint_NullOutput_ReturnsNull()
    {
        var hint = RegistryPushFailureDiagnostics.BuildHint(
            "host.docker.internal:5050", null, wasRewritten: true);

        hint.Should().BeNull();
    }
}

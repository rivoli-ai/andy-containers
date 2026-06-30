using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// conductor#2273 (robust artifact handoff). andy-containers uploads each run's
// output artifacts to andy-docs (FilesystemOutputArtifactCollector). Without
// the andy-docs API scope on its M2M client, every upload 401s and the
// cross-container handoff (EX.7) is broken. This guard pins the scope into the
// service registration manifest so a future edit can't silently drop it.
public class ServiceManifestScopeTests
{
    [Fact]
    public void ApiClient_Grants_AndyDocsScope()
    {
        var manifestPath = LocateRegistrationManifest();
        File.Exists(manifestPath).Should().BeTrue($"registration.json must exist at {manifestPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var scopes = doc.RootElement
            .GetProperty("auth")
            .GetProperty("apiClient")
            .GetProperty("scopes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        scopes.Should().Contain("scp:urn:andy-docs-api",
            "the M2M client must be allowed to upload run artifacts to andy-docs");
    }

    // Walk up from this source file's directory to the repo root and resolve
    // config/registration.json — robust regardless of the test working dir.
    private static string LocateRegistrationManifest([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "registration.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate config/registration.json from the test source tree.");
    }
}

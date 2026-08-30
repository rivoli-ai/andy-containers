using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// rivoli-ai/conductor#1029 (M1.9.2). Pins the conductor-terminal
// base-image spec contract: the YAML at images/conductor-terminal/spec.yaml
// validates cleanly through YamlTemplateParser, mentions the four
// load-bearing files Conductor's M1.9.3 install pipeline expects, and
// declares `is-base: true` so the variant images (M1.9.4 / M1.9.5)
// can find it.
//
// Drift on any of these silently breaks the image-management flow —
// either the build fails server-side, or the variant images can't
// extend the base, or M1.9.6 ships an image the orchestrator can't
// resolve.
public class ConductorTerminalSpecTests
{
    private readonly YamlTemplateParser _parser = new();

    [Fact]
    public void Spec_ValidatesCleanly()
    {
        var path = LocateSpec();
        var yaml = File.ReadAllText(path);

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue(
            $"the conductor-terminal spec must validate cleanly. Errors: {string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}"))}");
    }

    [Fact]
    public void Spec_DeclaresCanonicalIdentity()
    {
        var path = LocateSpec();
        var yaml = File.ReadAllText(path);

        // Pin the code + base_image — the variants reference these
        // by string, so a casual rename would break the chain.
        yaml.Should().Contain("code: conductor-terminal",
            "the variant images extend by `code`; renaming this without updating M1.9.4 / M1.9.5 silently orphans them");
        yaml.Should().Contain("base_image: ubuntu:22.04",
            "every assistant CLI's compatibility matrix targets 22.04; bumping to 24.04 needs re-validation");
        yaml.Should().Contain("is-base: true",
            "the `is-base` marker is what variant specs filter on when locating the base; drift = orphans");
    }

    [Fact]
    public void Spec_DeclaresAllInstallScriptHandoffFiles()
    {
        // The entrypoint, .bashrc, and .tmux.conf are part of the
        // shipped image. The spec's `files:` section is what tells
        // the andy-containers build pipeline to copy them; missing
        // any of these means the pipeline-built image differs from
        // the local docker-build (which DOES copy them via the
        // Dockerfile). Drift = "works on my machine".
        var path = LocateSpec();
        var yaml = File.ReadAllText(path);

        var requiredFiles = new[]
        {
            "/opt/conductor/entrypoint.sh",
            "/opt/conductor/install-assistants.sh",
            "/home/conductor/.bashrc",
            "/home/conductor/.tmux.conf",
        };

        foreach (var f in requiredFiles)
        {
            yaml.Should().Contain($"dest: {f}",
                $"`{f}` must be declared in the spec's `files:` section so the pipeline build mirrors the Dockerfile");
        }
    }

    [Fact]
    public void Spec_HasEveryShellAndVcsPackageEntrypointReliesOn()
    {
        // Mirror of the Dockerfile's apt install list. The Dockerfile
        // is the source of truth at `docker build` time; this spec is
        // the source of truth when the andy-containers build pipeline
        // assembles the image from declarative inputs. The two MUST
        // produce identical images — a missing package on this side
        // means the pipeline-built image lacks tools the user expects.
        var path = LocateSpec();
        var yaml = File.ReadAllText(path);

        var requiredPackages = new[]
        {
            "ca-certificates", "curl", "wget", "gnupg",
            "git", "bash", "zsh", "vim", "nano", "tmux",
            "sudo", "locales", "openssh-client",
        };

        foreach (var pkg in requiredPackages)
        {
            yaml.Should().Contain($"- {pkg}",
                $"package `{pkg}` is in the Dockerfile but missing from the spec's `packages:` list — the pipeline build would diverge from `docker build`");
        }
    }

    [Fact]
    public void Spec_CreatesConductorUserWithCorrectUid()
    {
        // uid 1000 matches the default macOS user mapping so
        // bind-mounted volumes from Docker Desktop don't end up
        // owned by root inside the container. Drift here breaks
        // workspace mounts silently — the user opens their repo and
        // every file is read-only.
        var path = LocateSpec();
        var yaml = File.ReadAllText(path);

        yaml.Should().Contain("--uid 1000",
            "uid 1000 is the load-bearing mapping for Docker Desktop bind-mounts; the variant images depend on it");
        yaml.Should().Contain("useradd",
            "user creation must happen in the install: block so the pipeline build provisions the same user as the Dockerfile");
    }

    private static string LocateSpec()
    {
        // Walk up from this source file's directory until we hit the
        // repo root (marked by a `images/conductor-terminal/spec.yaml`).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "images", "conductor-terminal", "spec.yaml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "could not locate images/conductor-terminal/spec.yaml — has the repo layout changed?");
    }
}

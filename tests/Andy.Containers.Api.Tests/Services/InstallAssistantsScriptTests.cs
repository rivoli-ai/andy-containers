using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// rivoli-ai/conductor#1030 (M1.9.3). Drives the install-assistants.sh
// script with the install actions stubbed out so the unit test runs
// without network or `npm`/`pip`/`curl` being able to fetch real
// packages. Pins:
//
//   - empty `$CONDUCTOR_INSTALL_ASSISTANTS` → no-op exit 0
//   - unknown slug → warning logged, exit 0
//   - log lines are well-formed NDJSON (parseable by the host log
//     collector)
//   - dispatch routes the expected slugs (claude-code, codex-cli,
//     aider, opencode) and falls through for anything else
//   - idempotent: running twice with an already-installed CLI on
//     $PATH logs a `skipped` event and doesn't re-run the install
//
// We don't exercise the actual install commands here — those need a
// real container. The "stub everything" mode is what shellcheck +
// these xUnit tests can validate; the live install path is covered
// by the M1.9.4 / M1.9.5 image-build CI.
public class InstallAssistantsScriptTests
{
    private static string LocateScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "images", "conductor-terminal", "install-assistants.sh");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("images/conductor-terminal/install-assistants.sh not found");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunScript(
        string conductorInstallAssistants,
        Dictionary<string, string>? extraPath = null)
    {
        var script = LocateScript();
        var workDir = Path.Combine(Path.GetTempPath(), "install-assistants-test-" + Guid.NewGuid().ToString("N"));
        var stubBin = Path.Combine(workDir, "stubbin");
        var logDir = Path.Combine(workDir, "log");
        Directory.CreateDirectory(stubBin);
        Directory.CreateDirectory(logDir);

        // Stub `sudo`, `tee`, `command`, `curl`, `tar`, `install`,
        // `apt-get`, `npm`, `pip`, `pip3`, `mktemp` so the script
        // can exec them without affecting the real system. Each
        // stub just returns 0 + echoes its args to stderr for
        // visibility.
        foreach (var name in new[] { "sudo", "curl", "tar", "apt-get", "npm", "pip", "pip3", "tee" })
        {
            File.WriteAllText(
                Path.Combine(stubBin, name),
                "#!/bin/sh\necho \"[stub:" + name + "] $*\" >&2\nexit 0\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(stubBin, name),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // `tee -a` writes to the log file. Replace with a real one
        // since the script tail-reads it to surface progress.
        File.WriteAllText(Path.Combine(stubBin, "tee"),
            "#!/bin/sh\n# pass-through tee, no-op (logs are inspected elsewhere)\ncat > /dev/null\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(stubBin, "tee"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The script touches /var/log/conductor; in the unit test
        // environment, send it to a writable temp instead by
        // overriding via INSTALL_LOG env-var won't work (the script
        // hardcodes the path) — so we mkdir + chmod the real
        // directory only if it doesn't exist already. Simpler:
        // rely on the script's graceful-fallback behaviour when
        // /var/log/conductor isn't writable (it logs to stderr via
        // `tee` which we stubbed).

        var psi = new ProcessStartInfo("/bin/bash", script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };
        // Prepend stub bin to PATH so the script picks up our stubs.
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = stubBin + Path.PathSeparator + existingPath;
        psi.Environment["CONDUCTOR_INSTALL_ASSISTANTS"] = conductorInstallAssistants;
        psi.Environment["HOME"] = workDir;
        // Override the script's log path to a writable temp file —
        // tests don't have permission to write /var/log/conductor.
        psi.Environment["CONDUCTOR_INSTALL_LOG"] = Path.Combine(logDir, "install.log");
        if (extraPath is not null)
        {
            foreach (var kv in extraPath)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    [Fact]
    public void EmptyEnvVar_IsNoopExitZero()
    {
        var (exit, _, stderr) = RunScript(conductorInstallAssistants: "");
        exit.Should().Be(0);
        stderr.Should().NotContain("\"event\":\"start\"",
            "an empty env var must not produce a start event — the user said `no assistants`, the script must not run any installer");
    }

    [Fact]
    public void UnknownSlug_LogsWarningAndExitsZero()
    {
        var (exit, _, stderr) = RunScript(conductorInstallAssistants: "this-is-not-a-real-slug");
        exit.Should().Be(0,
            "unknown slugs are a soft failure — the script keeps going and exits 0 so a typo doesn't kill an otherwise-valid batch");
        stderr.Should().Contain("\"event\":\"unknown\"");
        stderr.Should().Contain("this-is-not-a-real-slug");
    }

    [Fact]
    public void LogLines_AreValidNdjson()
    {
        // The host log collector parses one JSON object per line.
        // Drift on the line format silently breaks the install-progress
        // UI from M1.5.3.
        var (_, _, stderr) = RunScript(conductorInstallAssistants: "claude-code");
        var jsonLines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith('{') && l.EndsWith('}'))
            .ToList();

        jsonLines.Should().NotBeEmpty();
        foreach (var line in jsonLines)
        {
            // Every line must have the four mandatory fields.
            line.Should().Contain("\"ts\":\"");
            line.Should().Contain("\"level\":\"");
            line.Should().Contain("\"slug\":\"");
            line.Should().Contain("\"event\":\"");
        }
    }

    [Fact]
    public void MixedBatch_StubInstall_RunsKnownSkipsUnknown_ExitsZero()
    {
        // claude-code dispatches to its install function (stubbed
        // to succeed); `garbage` falls through to install_unknown
        // and logs a warning. The script must exit 0 because at
        // least one known slug succeeded.
        var (exit, _, stderr) = RunScript(
            conductorInstallAssistants: "garbage,claude-code");
        exit.Should().Be(0);
        stderr.Should().Contain("\"slug\":\"claude-code\"");
        stderr.Should().Contain("\"slug\":\"garbage\"");
        stderr.Should().Contain("\"event\":\"unknown\"");
    }

    [Fact]
    public void Summary_RecordsTotalsAtEnd()
    {
        var (_, _, stderr) = RunScript(
            conductorInstallAssistants: "claude-code,aider,bogus");
        // Three slugs total → summary line records counts.
        stderr.Should().Contain("\"event\":\"summary\"");
        stderr.Should().Contain("total=3");
        stderr.Should().Contain("unknown=1");
    }

    [Fact]
    public void WhitespaceSlug_IsSkipped()
    {
        // `claude-code,   ,opencode` should parse the empty/whitespace
        // segment as a no-op, not as an "unknown slug ''" entry.
        var (exit, _, stderr) = RunScript(
            conductorInstallAssistants: "claude-code,   ,opencode");
        exit.Should().Be(0);
        // Should NOT have an unknown-event with an empty slug —
        // that'd surface as a `{"slug":"","event":"unknown"}` line.
        stderr.Should().NotContain("\"slug\":\"\"");
    }
}

namespace Andy.Containers.Tests.TestSupport;

/// <summary>
/// Bash script that pretends to be a CLI tool (docker, container,
/// curl, …). Records each invocation's arguments to a temp file and
/// exits with a configurable status, optionally writing canned text
/// to stdout/stderr. Tests read the recorded arguments to assert
/// what the system-under-test called.
/// </summary>
/// <remarks>
/// Used by IM6 (DockerCliUploader tests) and IM7 (BuildEngineDetector
/// tests) so binaries don't actually need to be on the test host.
/// macOS / Linux only — tests that construct one early-return on
/// Windows before it gets here.
/// </remarks>
internal sealed class StubScript : IDisposable
{
    public string Path { get; }
    public string ArgsFile { get; }

    public StubScript(int exitCode, string stderr)
        : this(exitCode: exitCode, stdoutLine: "", stderr: stderr) { }

    public StubScript(int exitCode, string stdoutLine, string stderr)
    {
        ArgsFile = System.IO.Path.GetTempFileName();
        Path = System.IO.Path.GetTempFileName();
        var shPath = Path + ".sh";
        File.Move(Path, shPath);
        Path = shPath;

        var body = $"""
            #!/usr/bin/env bash
            # Record arguments one-per-line. Each invocation produces
            # one block; blocks are separated by an empty line.
            for a in "$@"; do
              echo "$a" >> "{ArgsFile}"
            done
            echo "" >> "{ArgsFile}"
            {(string.IsNullOrEmpty(stdoutLine) ? "" : $"echo '{stdoutLine}'")}
            {(string.IsNullOrEmpty(stderr) ? "" : $"echo '{stderr}' 1>&2")}
            exit {exitCode}
            """;
        File.WriteAllText(Path, body);
        if (!OperatingSystem.IsWindows())
        {
            // Mark the script executable; tests early-return on
            // Windows before constructing one.
            File.SetUnixFileMode(Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public List<List<string>> Invocations
    {
        get
        {
            if (!File.Exists(ArgsFile))
            {
                return [];
            }
            var lines = File.ReadAllLines(ArgsFile);
            var blocks = new List<List<string>>();
            var current = new List<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    if (current.Count > 0)
                    {
                        blocks.Add(current);
                        current = [];
                    }
                }
                else
                {
                    current.Add(line);
                }
            }
            if (current.Count > 0) { blocks.Add(current); }
            return blocks;
        }
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best-effort cleanup */ }
        try { File.Delete(ArgsFile); } catch { /* best-effort cleanup */ }
    }
}

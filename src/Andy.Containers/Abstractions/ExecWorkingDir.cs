namespace Andy.Containers.Abstractions;

/// <summary>
/// Shared composition of the optional working-directory facet of the exec
/// contract (rivoli-ai/andy-containers, exec working-dir feature).
///
/// <para>
/// The exec endpoint historically ran <c>sh -c "&lt;command&gt;"</c> with NO
/// working directory, so every command ran in the image's default WORKDIR
/// rather than the repo checkout (<c>/workspace</c>). Two shipped fixes worked
/// around this by prefixing <c>cd '&lt;dir&gt;' &amp;&amp; </c> at the caller
/// (andy-tasks#383 verifier path, andy-containers#360 HeadlessRunner). This
/// makes the working directory a first-class field on the exec contract.
/// </para>
///
/// <para>
/// Providers that expose a native working-directory mechanism (Docker's
/// <c>ContainerExecCreateParameters.WorkingDir</c>, i.e. <c>docker exec -w</c>)
/// should prefer it and ignore <see cref="Wrap"/>. Providers that only carry a
/// command string (Apple <c>container exec sh -c</c>, the SSH/cloud paths) wrap
/// the command with <see cref="Wrap"/>.
/// </para>
///
/// <para>
/// <b>Backward compatibility is mandatory:</b> a null/whitespace
/// <c>workingDir</c> returns the command byte-identical, so existing callers
/// (including andy-tasks' own <c>cd</c>-prefix workaround) are unaffected.
/// </para>
/// </summary>
public static class ExecWorkingDir
{
    /// <summary>
    /// Wraps <paramref name="command"/> so it runs inside
    /// <paramref name="workingDir"/> via <c>cd '&lt;dir&gt;' &amp;&amp;
    /// &lt;command&gt;</c>. The directory is single-quote-escaped for
    /// <c>/bin/sh -c</c>. When <paramref name="workingDir"/> is null/empty/
    /// whitespace the command is returned unchanged (the pre-existing
    /// no-working-dir behaviour).
    /// </summary>
    public static string Wrap(string command, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
        {
            return command;
        }

        return $"cd {ShellSingleQuote(workingDir.Trim())} && {command}";
    }

    /// <summary>
    /// POSIX single-quote escape — safe for <c>/bin/sh -c "..."</c>. A single
    /// quote inside the value closes the quote, inserts an escaped literal
    /// quote (<c>'\''</c>), then reopens.
    /// </summary>
    private static string ShellSingleQuote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";
}

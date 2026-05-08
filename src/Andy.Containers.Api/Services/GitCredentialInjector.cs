using System.Text;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// #1046. Pure-function builder for the shell script that
/// <c>ContainerProvisioningWorker</c> runs inside a freshly-provisioned
/// container to materialise the user's <see cref="GitCredential"/>s.
///
/// Without this step, <see cref="GitCloneService"/>'s initial template
/// clone uses the credentials once (via the embedded
/// <c>https://&lt;token&gt;@host/...</c> URL) and discards them. Manual
/// <c>git clone</c> commands the user runs from inside the container —
/// in a terminal, in code-server, in an agent run — would then fail on
/// any private remote because no credentials reached the container's
/// filesystem or git config.
///
/// Per-type wiring:
/// - <c>PersonalAccessToken</c> / <c>OAuthToken</c> → write
///   <c>~/.git-credentials</c> (mode 0600) and run
///   <c>git config --global credential.helper store</c>.
/// - <c>DeployKey</c> → write <c>~/.ssh/id_&lt;label&gt;</c> (mode 0600)
///   and append a <c>Host</c> stanza to <c>~/.ssh/config</c>.
///
/// The output script is wrapped in a single <c>su - &lt;containerUser&gt; -c '…'</c>
/// invocation so all paths land in the right home directory and the
/// non-root user owns the resulting files. <see cref="ContainerProvisioningWorker"/>
/// already follows this pattern for git-config (user.name / user.email),
/// so this fits the existing wiring.
/// </summary>
internal static class GitCredentialInjector
{
    /// <summary>
    /// Builds the shell script. Returns null when there's nothing to
    /// inject (empty list, or no credential survived shape validation).
    /// </summary>
    /// <param name="containerUser">Target user inside the container — typically <c>job.ContainerUser</c>.</param>
    /// <param name="credentials">Decrypted credentials, in any order.</param>
    public static string? BuildInjectionScript(
        string containerUser,
        IReadOnlyList<DecryptedGitCredential> credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerUser);
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.Count == 0) return null;

        var inner = new StringBuilder();
        var anyCredentialPart = false;

        // .git-credentials line per PAT / OAuth credential.
        var gitCredentialsLines = new List<string>();
        foreach (var cred in credentials)
        {
            if (cred.CredentialType is GitCredentialType.PersonalAccessToken
                                    or GitCredentialType.OAuthToken)
            {
                var line = BuildGitCredentialsLine(cred);
                if (line is not null)
                {
                    gitCredentialsLines.Add(line);
                }
            }
        }
        if (gitCredentialsLines.Count > 0)
        {
            inner.AppendLine("mkdir -p ~");
            // Heredoc body — single-quoted EOF prevents shell expansion
            // so a token containing $ / ` characters round-trips intact.
            inner.AppendLine("cat > ~/.git-credentials <<'EOF'");
            foreach (var line in gitCredentialsLines)
            {
                inner.AppendLine(line);
            }
            inner.AppendLine("EOF");
            inner.AppendLine("chmod 0600 ~/.git-credentials");
            inner.AppendLine("git config --global credential.helper store");
            anyCredentialPart = true;
        }

        // SSH key per DeployKey credential.
        var sshConfigStanzas = new List<string>();
        var keyFileWrites = new List<string>();
        foreach (var cred in credentials)
        {
            if (cred.CredentialType != GitCredentialType.DeployKey) continue;
            var keyFilename = SafeKeyFilename(cred.Label);
            keyFileWrites.Add(BuildKeyFileBlock(keyFilename, cred.PlaintextToken));

            // Per-host stanza only when the credential is host-scoped;
            // otherwise we rely on `IdentityFile` defaulting from the
            // user's `~/.ssh` plus standard ssh-agent discovery.
            if (!string.IsNullOrWhiteSpace(cred.GitHost))
            {
                sshConfigStanzas.Add(
                    $"Host {cred.GitHost}\n" +
                    $"    IdentityFile ~/.ssh/{keyFilename}\n" +
                    "    IdentitiesOnly yes\n");
            }
        }
        if (keyFileWrites.Count > 0)
        {
            inner.AppendLine("mkdir -p ~/.ssh");
            inner.AppendLine("chmod 0700 ~/.ssh");
            foreach (var block in keyFileWrites)
            {
                inner.Append(block);
            }
            if (sshConfigStanzas.Count > 0)
            {
                inner.AppendLine("touch ~/.ssh/config");
                inner.AppendLine("cat >> ~/.ssh/config <<'EOF'");
                foreach (var stanza in sshConfigStanzas)
                {
                    inner.Append(stanza);
                }
                inner.AppendLine("EOF");
                inner.AppendLine("chmod 0600 ~/.ssh/config");
            }
            anyCredentialPart = true;
        }

        if (!anyCredentialPart) return null;

        // Wrap in `su - <user> -c '…'` so files land in the user's home,
        // owned by them. Single-quote escaping inside the wrapped script
        // uses the standard `'\''` trick.
        var escapedInner = inner.ToString().Replace("'", "'\\''");
        return $"su - {containerUser} -c '{escapedInner}'";
    }

    /// <summary>
    /// Builds one git-credentials line per RFC-style format:
    /// <c>https://&lt;encoded-token&gt;@&lt;host&gt;</c>.
    /// Returns null when the credential lacks the host context git's
    /// store-helper needs (no <c>GitHost</c> means we can't form a
    /// sensible URL — git would still match by username but emitting
    /// a hostless line is more confusing than skipping).
    /// </summary>
    private static string? BuildGitCredentialsLine(DecryptedGitCredential cred)
    {
        if (string.IsNullOrWhiteSpace(cred.GitHost)) return null;
        var encodedToken = Uri.EscapeDataString(cred.PlaintextToken);
        var username = cred.CredentialType == GitCredentialType.OAuthToken
            ? "oauth2"
            : "x-access-token";
        return $"https://{username}:{encodedToken}@{cred.GitHost}";
    }

    /// <summary>
    /// Build the <c>cat &gt; key &lt;&lt; EOF</c> + chmod block for one
    /// SSH private key. The single-quoted heredoc prevents shell
    /// expansion of any <c>$</c> or backtick characters in the PEM
    /// body (they don't appear in well-formed keys, but defensive
    /// against an attacker-crafted credential row).
    /// </summary>
    private static string BuildKeyFileBlock(string filename, string keyContent)
    {
        var block = new StringBuilder();
        block.AppendLine($"cat > ~/.ssh/{filename} <<'EOF'");
        // Some keys arrive without a trailing newline; OpenSSH requires
        // one. Add it defensively.
        block.AppendLine(keyContent.TrimEnd());
        block.AppendLine("EOF");
        block.AppendLine($"chmod 0600 ~/.ssh/{filename}");
        return block.ToString();
    }

    /// <summary>
    /// Reduce a credential's user-supplied <c>Label</c> to a safe
    /// filename for <c>~/.ssh/&lt;name&gt;</c>. Strips any character
    /// outside <c>[A-Za-z0-9_.-]</c> and prepends <c>id_</c> so the
    /// file name signals its purpose to a casual reader. Empty or
    /// fully-stripped labels degrade to <c>id_deploykey</c>.
    /// </summary>
    private static string SafeKeyFilename(string label)
    {
        var sb = new StringBuilder("id_", capacity: label.Length + 3);
        foreach (var ch in label)
        {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '_' || ch == '.' || ch == '-')
            {
                sb.Append(ch);
            }
        }
        if (sb.Length == "id_".Length)
        {
            sb.Append("deploykey");
        }
        return sb.ToString();
    }
}

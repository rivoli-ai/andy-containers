using System.Text;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class SshProvisioningService : ISshProvisioningService
{
    public string GenerateSetupScript(SshConfig config, IReadOnlyList<string> publicKeys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine();
        sb.AppendLine("# Install openssh-server if not present");
        sb.AppendLine("if ! command -v sshd &>/dev/null; then");
        sb.AppendLine("  apt-get update -qq && apt-get install -y -qq openssh-server 2>/dev/null || true");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Ensure runtime directory exists");
        sb.AppendLine("mkdir -p /run/sshd");
        sb.AppendLine();
        sb.AppendLine("# Generate host keys if missing");
        sb.AppendLine("ssh-keygen -A 2>/dev/null || true");
        sb.AppendLine();

        // Configure sshd
        sb.AppendLine("# Configure sshd");
        sb.AppendLine("mkdir -p /etc/ssh/sshd_config.d");
        sb.AppendLine("cat > /etc/ssh/sshd_config.d/andy-containers.conf << 'SSHCONF'");
        sb.AppendLine($"Port {config.Port}");
        sb.AppendLine($"PermitRootLogin {(config.RootLogin ? "yes" : "no")}");
        sb.AppendLine($"PubkeyAuthentication {(config.AuthMethods.Contains("public_key") ? "yes" : "no")}");
        sb.AppendLine($"PasswordAuthentication {(config.AuthMethods.Contains("password") ? "yes" : "no")}");
        if (config.IdleTimeoutMinutes > 0)
        {
            sb.AppendLine("ClientAliveInterval 60");
            sb.AppendLine($"ClientAliveCountMax {config.IdleTimeoutMinutes}");
        }
        sb.AppendLine("SSHCONF");
        sb.AppendLine();

        // Setup authorized keys
        if (publicKeys.Count > 0)
        {
            sb.AppendLine("# Setup authorized keys for dev user");
            sb.AppendLine("mkdir -p /home/dev/.ssh");
            sb.AppendLine("chmod 700 /home/dev/.ssh");
            sb.AppendLine("cat > /home/dev/.ssh/authorized_keys << 'AUTHKEYS'");
            foreach (var key in publicKeys)
            {
                sb.AppendLine(key);
            }
            sb.AppendLine("AUTHKEYS");
            sb.AppendLine("chmod 600 /home/dev/.ssh/authorized_keys");
            sb.AppendLine("chown -R dev:dev /home/dev/.ssh 2>/dev/null || true");
            sb.AppendLine();
        }

        // Start sshd
        sb.AppendLine("# Start sshd if not running");
        sb.AppendLine("if ! pgrep -x sshd > /dev/null; then");
        sb.AppendLine("  /usr/sbin/sshd");
        sb.AppendLine("fi");

        return sb.ToString();
    }
}

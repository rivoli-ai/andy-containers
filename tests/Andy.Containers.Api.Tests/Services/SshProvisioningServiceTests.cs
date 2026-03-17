using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class SshProvisioningServiceTests
{
    private readonly SshProvisioningService _service = new();

    private static SshConfig DefaultConfig() => new()
    {
        Enabled = true,
        Port = 22,
        AuthMethods = ["public_key"],
        RootLogin = false,
        IdleTimeoutMinutes = 60
    };

    [Fact]
    public void GenerateSetupScript_ContainsBashShebang()
    {
        var script = _service.GenerateSetupScript(DefaultConfig(), []);

        script.Should().StartWith("#!/bin/bash");
    }

    [Fact]
    public void GenerateSetupScript_InstallsOpenSsh()
    {
        var script = _service.GenerateSetupScript(DefaultConfig(), []);

        script.Should().Contain("openssh-server");
    }

    [Fact]
    public void GenerateSetupScript_GeneratesHostKeys()
    {
        var script = _service.GenerateSetupScript(DefaultConfig(), []);

        script.Should().Contain("ssh-keygen -A");
    }

    [Fact]
    public void GenerateSetupScript_ConfiguresPort()
    {
        var config = DefaultConfig();
        config.Port = 2222;

        var script = _service.GenerateSetupScript(config, []);

        script.Should().Contain("Port 2222");
    }

    [Fact]
    public void GenerateSetupScript_DisablesRootLogin()
    {
        var config = DefaultConfig();
        config.RootLogin = false;

        var script = _service.GenerateSetupScript(config, []);

        script.Should().Contain("PermitRootLogin no");
    }

    [Fact]
    public void GenerateSetupScript_EnablesRootLogin()
    {
        var config = DefaultConfig();
        config.RootLogin = true;

        var script = _service.GenerateSetupScript(config, []);

        script.Should().Contain("PermitRootLogin yes");
    }

    [Fact]
    public void GenerateSetupScript_EnablesPubkeyAuth()
    {
        var config = DefaultConfig();
        config.AuthMethods = ["public_key"];

        var script = _service.GenerateSetupScript(config, []);

        script.Should().Contain("PubkeyAuthentication yes");
        script.Should().Contain("PasswordAuthentication no");
    }

    [Fact]
    public void GenerateSetupScript_EnablesPasswordAuth()
    {
        var config = DefaultConfig();
        config.AuthMethods = ["password"];

        var script = _service.GenerateSetupScript(config, []);

        script.Should().Contain("PubkeyAuthentication no");
        script.Should().Contain("PasswordAuthentication yes");
    }

    [Fact]
    public void GenerateSetupScript_SetsIdleTimeout()
    {
        var config = DefaultConfig();
        config.IdleTimeoutMinutes = 30;

        var script = _service.GenerateSetupScript(config, []);

        script.Should().Contain("ClientAliveInterval 60");
        script.Should().Contain("ClientAliveCountMax 30");
    }

    [Fact]
    public void GenerateSetupScript_NoTimeoutWhenZero()
    {
        var config = DefaultConfig();
        config.IdleTimeoutMinutes = 0;

        var script = _service.GenerateSetupScript(config, []);

        script.Should().NotContain("ClientAliveInterval");
    }

    [Fact]
    public void GenerateSetupScript_InjectsAuthorizedKeys()
    {
        var keys = new List<string>
        {
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKey1 user@laptop",
            "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQKey2 ci@server"
        };

        var script = _service.GenerateSetupScript(DefaultConfig(), keys);

        script.Should().Contain("authorized_keys");
        script.Should().Contain("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKey1 user@laptop");
        script.Should().Contain("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQKey2 ci@server");
        script.Should().Contain("chmod 600 /home/dev/.ssh/authorized_keys");
    }

    [Fact]
    public void GenerateSetupScript_NoKeysSection_WhenNoPublicKeys()
    {
        var script = _service.GenerateSetupScript(DefaultConfig(), []);

        script.Should().NotContain("authorized_keys");
    }

    [Fact]
    public void GenerateSetupScript_StartsSshd()
    {
        var script = _service.GenerateSetupScript(DefaultConfig(), []);

        script.Should().Contain("/usr/sbin/sshd");
    }
}

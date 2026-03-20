using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class TemplateYamlValidatorTests
{
    private readonly TemplateYamlValidator _validator = new();

    private const string ValidBaseYaml = """
        code: test-template
        version: "1.0"
        base_image: ubuntu:24.04
        """;

    private string WithSsh(string sshSection) => $"""
        {ValidBaseYaml}
        {sshSection}
        """;

    // === Validation: Valid SSH configs ===

    [Fact]
    public async Task Validate_ValidSshConfig_NoErrors()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
              root_login: false
              idle_timeout_minutes: 60
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_SshDisabled_NoFieldValidation()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: false
              port: 0
              auth_methods: []
              idle_timeout_minutes: -999
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_NoSshSection_NoErrors()
    {
        var result = await _validator.ValidateYamlAsync(ValidBaseYaml);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // === Validation: Port errors ===

    [Fact]
    public async Task Validate_SshPort0_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 0
              auth_methods:
                - public_key
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.port");
    }

    [Fact]
    public async Task Validate_SshPort70000_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 70000
              auth_methods:
                - public_key
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.port");
    }

    // === Validation: Auth methods errors ===

    [Fact]
    public async Task Validate_EmptyAuthMethods_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods: []
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.auth_methods");
    }

    [Fact]
    public async Task Validate_InvalidAuthMethod_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - keyboard
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.auth_methods" && e.Message.Contains("keyboard"));
    }

    [Fact]
    public async Task Validate_MissingAuthMethods_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.auth_methods");
    }

    // === Validation: Idle timeout errors ===

    [Fact]
    public async Task Validate_IdleTimeout2000_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
              idle_timeout_minutes: 2000
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.idle_timeout_minutes");
    }

    [Fact]
    public async Task Validate_IdleTimeoutNegative_Error()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
              idle_timeout_minutes: -1
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.idle_timeout_minutes");
    }

    // === Validation: Port conflict ===

    [Fact]
    public async Task Validate_SshPortConflictsWithDeclaredPort_Error()
    {
        var yaml = """
            code: test-template
            version: "1.0"
            base_image: ubuntu:24.04
            ports:
              22: http
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().Contain(e => e.Field == "ssh.port" && e.Message.Contains("conflicts"));
    }

    [Fact]
    public async Task Validate_SshPortDifferentFromDeclaredPorts_NoConflictError()
    {
        var yaml = """
            code: test-template
            version: "1.0"
            base_image: ubuntu:24.04
            ports:
              8080: http
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Where(e => e.Field == "ssh.port").Should().BeEmpty();
    }

    // === Warnings ===

    [Fact]
    public async Task Validate_PasswordAuth_Warning()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - password
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Warnings.Should().Contain(w => w.Field == "ssh.auth_methods" && w.Message.Contains("Password"));
    }

    [Fact]
    public async Task Validate_PublicKeyOnly_NoPasswordWarning()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Warnings.Where(w => w.Field == "ssh.auth_methods").Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_RootLoginTrue_Warning()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
              root_login: true
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Warnings.Should().Contain(w => w.Field == "ssh.root_login" && w.Message.Contains("Root"));
    }

    [Fact]
    public async Task Validate_RootLoginFalse_NoWarning()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
              root_login: false
            """);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Warnings.Where(w => w.Field == "ssh.root_login").Should().BeEmpty();
    }

    // === Parsing ===

    [Fact]
    public async Task Parse_SshSection_PopulatesSshConfiguration()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 2222
              auth_methods:
                - public_key
                - password
              root_login: true
              idle_timeout_minutes: 120
            """);

        var template = await _validator.ParseYamlToTemplateAsync(yaml);

        template.SshConfiguration.Should().NotBeNull();
        var config = JsonSerializer.Deserialize<SshConfig>(template.SshConfiguration!);
        config.Should().NotBeNull();
        config!.Enabled.Should().BeTrue();
        config.Port.Should().Be(2222);
        config.AuthMethods.Should().BeEquivalentTo(["public_key", "password"]);
        config.RootLogin.Should().BeTrue();
        config.IdleTimeoutMinutes.Should().Be(120);
    }

    [Fact]
    public async Task Parse_NoSshSection_SshConfigurationIsNull()
    {
        var template = await _validator.ParseYamlToTemplateAsync(ValidBaseYaml);

        template.SshConfiguration.Should().BeNull();
    }

    [Fact]
    public async Task Parse_SshSectionJson_ContainsAllFields()
    {
        var yaml = WithSsh("""
            ssh:
              enabled: true
              port: 22
              auth_methods:
                - public_key
              root_login: false
              idle_timeout_minutes: 60
            """);

        var template = await _validator.ParseYamlToTemplateAsync(yaml);

        template.SshConfiguration.Should().NotBeNull();
        var json = JsonDocument.Parse(template.SshConfiguration!);
        json.RootElement.TryGetProperty("Enabled", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("Port", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("AuthMethods", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("RootLogin", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("IdleTimeoutMinutes", out _).Should().BeTrue();
    }
}

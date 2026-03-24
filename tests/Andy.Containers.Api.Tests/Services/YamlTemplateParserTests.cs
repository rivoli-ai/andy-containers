using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class YamlTemplateParserTests
{
    private readonly YamlTemplateParser _parser = new();

    private const string MinimalValidYaml = """
        code: my-template
        name: My Template
        version: 1.0.0
        base_image: ubuntu:24.04
        """;

    private const string FullYaml = """
        code: full-stack-dev
        name: Full Stack Development
        description: A full stack development environment
        version: 2.1.0
        base_image: mcr.microsoft.com/dotnet/sdk:8.0
        scope: Organization
        ide_type: Both
        gpu_required: true
        gpu_preferred: false
        tags:
          - dotnet
          - python
        ports:
          - 8080
          - 3000
        environment:
          DOTNET_ROOT: /usr/share/dotnet
          NODE_ENV: development
        scripts:
          setup: echo hello
        resources:
          cpu: "2"
          memory: 4Gi
        dependencies:
          - name: dotnet-sdk
            type: sdk
            version: "8.0"
          - name: node
            type: runtime
            version: "20"
        git_repositories:
          - url: https://github.com/example/repo.git
        """;

    // --- Validation tests ---

    [Fact]
    public void Validate_ValidYaml_ReturnsIsValidTrue()
    {
        var result = _parser.Validate(MinimalValidYaml);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidYamlSyntax_ReturnsErrorWithLineNumber()
    {
        var yaml = """
            code: test
            name: [invalid yaml
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Field.Should().Be("yaml");
        result.Errors[0].Line.Should().NotBeNull();
    }

    [Fact]
    public void Validate_MissingCode_ReturnsError()
    {
        var yaml = """
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code");
    }

    [Fact]
    public void Validate_MissingName_ReturnsError()
    {
        var yaml = """
            code: test-template
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "name");
    }

    [Fact]
    public void Validate_MissingVersion_ReturnsError()
    {
        var yaml = """
            code: test-template
            name: Test
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "version");
    }

    [Fact]
    public void Validate_MissingBaseImage_ReturnsError()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "base_image");
    }

    [Fact]
    public void Validate_MultipleMissingFields_ReturnsMultipleErrors()
    {
        var yaml = "description: just a description";

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterOrEqualTo(4);
        result.Errors.Should().Contain(e => e.Field == "code");
        result.Errors.Should().Contain(e => e.Field == "name");
        result.Errors.Should().Contain(e => e.Field == "version");
        result.Errors.Should().Contain(e => e.Field == "base_image");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("has spaces")]
    [InlineData("UpperCase")]
    [InlineData("1starts-with-digit")]
    [InlineData("-starts-with-hyphen")]
    public void Validate_InvalidCodeFormat_ReturnsError(string code)
    {
        var yaml = $"""
            code: {code}
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("my-template")]
    [InlineData("template-123")]
    [InlineData("a1")]
    public void Validate_ValidCodeFormats_Pass(string code)
    {
        var yaml = $"""
            code: {code}
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().NotContain(e => e.Field == "code");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("abc")]
    [InlineData("1.0.0.0")]
    public void Validate_InvalidVersion_ReturnsError(string version)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: {version}
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "version");
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-alpha")]
    [InlineData("0.1.0")]
    public void Validate_ValidVersions_Pass(string version)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: {version}
            base_image: ubuntu:24.04
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().NotContain(e => e.Field == "version");
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("just-a-name")]
    public void Validate_InvalidBaseImage_ReturnsError(string image)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: 1.0.0
            base_image: {image}
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "base_image");
    }

    [Theory]
    [InlineData("ubuntu:24.04")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:8.0")]
    public void Validate_ValidBaseImage_Passes(string image)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: 1.0.0
            base_image: {image}
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().NotContain(e => e.Field == "base_image");
    }

    [Fact]
    public void Validate_InvalidDependencyType_ReturnsError()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - name: thing
                type: invalid-type
                version: "1.0"
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field.Contains("dependencies") && e.Field.Contains("type"));
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("runtime")]
    [InlineData("compiler")]
    [InlineData("tool")]
    [InlineData("library")]
    public void Validate_ValidDependencyTypes_Pass(string type)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - name: thing
                type: {type}
                version: "1.0"
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().NotContain(e => e.Field.Contains("dependencies"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Validate_InvalidPortNumber_ReturnsError(int port)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            ports:
              - {port}
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "ports");
    }

    [Fact]
    public void Validate_ValidPortNumbers_Pass()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            ports:
              - 80
              - 443
              - 8080
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().NotContain(e => e.Field == "ports");
    }

    [Fact]
    public void Validate_UnknownTopLevelKey_ProducesWarningNotError()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            unknown_key: some value
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().ContainSingle();
        result.Warnings[0].Field.Should().Be("unknown_key");
    }

    [Fact]
    public void Validate_InvalidScope_ReturnsError()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            scope: InvalidScope
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "scope");
    }

    [Fact]
    public void Validate_InvalidIdeType_ReturnsError()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            ide_type: InvalidType
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "ide_type");
    }

    // --- Parse tests ---

    [Fact]
    public void Parse_MinimalValidYaml_ReturnsContainerTemplate()
    {
        var template = _parser.Parse(MinimalValidYaml);

        template.Code.Should().Be("my-template");
        template.Name.Should().Be("My Template");
        template.Version.Should().Be("1.0.0");
        template.BaseImage.Should().Be("ubuntu:24.04");
    }

    [Fact]
    public void Parse_FullYaml_ReturnsAllFields()
    {
        var template = _parser.Parse(FullYaml);

        template.Code.Should().Be("full-stack-dev");
        template.Name.Should().Be("Full Stack Development");
        template.Description.Should().Be("A full stack development environment");
        template.Version.Should().Be("2.1.0");
        template.BaseImage.Should().Be("mcr.microsoft.com/dotnet/sdk:8.0");
        template.CatalogScope.Should().Be(CatalogScope.Organization);
        template.IdeType.Should().Be(IdeType.Both);
        template.GpuRequired.Should().BeTrue();
        template.GpuPreferred.Should().BeFalse();
        template.Tags.Should().BeEquivalentTo(new[] { "dotnet", "python" });
        template.Ports.Should().NotBeNull();
        template.EnvironmentVariables.Should().NotBeNull();
        template.Scripts.Should().NotBeNull();
        template.DefaultResources.Should().NotBeNull();
        template.Toolchains.Should().NotBeNull();
        template.GitRepositories.Should().NotBeNull();
    }

    [Fact]
    public void Parse_DependenciesToToolchainsJson()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - name: dotnet-sdk
                type: sdk
                version: "8.0"
            """;

        var template = _parser.Parse(yaml);

        template.Toolchains.Should().NotBeNull();
        template.Toolchains.Should().Contain("dotnet-sdk");
        template.Toolchains.Should().Contain("sdk");
        template.Toolchains.Should().Contain("8.0");
    }

    [Fact]
    public void Parse_TagsAsStringArray()
    {
        var yaml = """
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            tags:
              - alpha
              - beta
              - gamma
            """;

        var template = _parser.Parse(yaml);

        template.Tags.Should().BeEquivalentTo(new[] { "alpha", "beta", "gamma" });
    }

    [Theory]
    [InlineData("Global", CatalogScope.Global)]
    [InlineData("Organization", CatalogScope.Organization)]
    [InlineData("Team", CatalogScope.Team)]
    [InlineData("User", CatalogScope.User)]
    public void Parse_ScopeEnumCorrectly(string scopeValue, CatalogScope expected)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            scope: {scopeValue}
            """;

        var template = _parser.Parse(yaml);

        template.CatalogScope.Should().Be(expected);
    }

    [Theory]
    [InlineData("None", IdeType.None)]
    [InlineData("CodeServer", IdeType.CodeServer)]
    [InlineData("Zed", IdeType.Zed)]
    [InlineData("Both", IdeType.Both)]
    public void Parse_IdeTypeEnumCorrectly(string ideValue, IdeType expected)
    {
        var yaml = $"""
            code: test-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            ide_type: {ideValue}
            """;

        var template = _parser.Parse(yaml);

        template.IdeType.Should().Be(expected);
    }

    [Fact]
    public void Parse_DefaultsScopeToGlobal_WhenNotSpecified()
    {
        var template = _parser.Parse(MinimalValidYaml);

        template.CatalogScope.Should().Be(CatalogScope.Global);
    }

    [Fact]
    public void Parse_DefaultsIdeTypeToCodeServer_WhenNotSpecified()
    {
        var template = _parser.Parse(MinimalValidYaml);

        template.IdeType.Should().Be(IdeType.CodeServer);
    }

    [Fact]
    public void Parse_CodeAssistant_ParsedAsJson()
    {
        var yaml = """
            code: my-ai-template
            name: AI Template
            version: 1.0.0
            base_image: ubuntu:24.04
            code_assistant:
              tool: claude-code
              auto_start: false
              api_key_env: ANTHROPIC_API_KEY
            """;

        var template = _parser.Parse(yaml);

        template.CodeAssistant.Should().NotBeNull();
        template.CodeAssistant.Should().Contain("claude-code");
        template.CodeAssistant.Should().Contain("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void Parse_WithoutCodeAssistant_CodeAssistantIsNull()
    {
        var template = _parser.Parse(MinimalValidYaml);

        template.CodeAssistant.Should().BeNull();
    }

    [Fact]
    public void Validate_CodeAssistant_IsKnownKey()
    {
        var yaml = """
            code: my-ai-template
            name: AI Template
            version: 1.0.0
            base_image: ubuntu:24.04
            code_assistant:
              tool: claude-code
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().NotContain(w => w.Field == "code_assistant");
    }
}

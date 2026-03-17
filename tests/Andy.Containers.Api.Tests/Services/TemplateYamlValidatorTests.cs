using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class TemplateYamlValidatorTests
{
    private readonly TemplateYamlValidator _validator = new();

    private const string ValidYaml = """
        code: my-template
        name: My Template
        version: 1.0.0
        base_image: ubuntu:24.04
        """;

    [Fact]
    public async Task ValidYaml_ReturnsValid()
    {
        var result = await _validator.ValidateYamlAsync(ValidYaml);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyYaml_ReturnsInvalid()
    {
        var result = await _validator.ValidateYamlAsync("");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Field == "yaml");
    }

    [Fact]
    public async Task NullYaml_ReturnsInvalid()
    {
        var result = await _validator.ValidateYamlAsync(null!);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task OversizedYaml_ReturnsInvalid()
    {
        var yaml = new string('a', 1_048_577);

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("maximum size"));
    }

    [Fact]
    public async Task InvalidYamlSyntax_ReturnsInvalid()
    {
        var result = await _validator.ValidateYamlAsync("{ invalid: yaml: [");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Invalid YAML syntax"));
    }

    // --- Required field: code ---

    [Fact]
    public async Task MissingCode_ReturnsError()
    {
        var yaml = """
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code");
    }

    [Theory]
    [InlineData("ab")]           // too short
    [InlineData("A-Invalid")]    // uppercase
    [InlineData("-bad-start")]   // starts with hyphen
    [InlineData("bad-end-")]     // ends with hyphen
    public async Task InvalidCodeFormat_ReturnsError(string code)
    {
        var yaml = $"""
            code: {code}
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "code");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("my-template")]
    [InlineData("dotnet-8-dev")]
    public async Task ValidCode_NoError(string code)
    {
        var yaml = $"""
            code: {code}
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().NotContain(e => e.Field == "code");
    }

    // --- Required field: version ---

    [Fact]
    public async Task MissingVersion_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            base_image: ubuntu:24.04
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "version");
    }

    [Theory]
    [InlineData("not-semver")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    public async Task InvalidSemver_ReturnsError(string version)
    {
        var yaml = $"""
            code: my-template
            name: Test
            version: {version}
            base_image: ubuntu:24.04
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "version");
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.1.0-beta.1")]
    [InlineData("10.20.30")]
    public async Task ValidSemver_NoError(string version)
    {
        var yaml = $"""
            code: my-template
            name: Test
            version: {version}
            base_image: ubuntu:24.04
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().NotContain(e => e.Field == "version");
    }

    // --- Resources ---

    [Fact]
    public async Task ResourceOutOfBounds_ReturnsErrors()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            resources:
              cpu_cores: 256
              memory_mb: 100
              disk_gb: 5000
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "resources.cpu_cores");
        result.Errors.Should().Contain(e => e.Field == "resources.memory_mb");
        result.Errors.Should().Contain(e => e.Field == "resources.disk_gb");
    }

    // --- Environment variables ---

    [Fact]
    public async Task InvalidEnvVarKey_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            environment:
              lowercase_bad: value
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field.StartsWith("environment."));
    }

    [Fact]
    public async Task ValidEnvVarKey_NoError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            environment:
              MY_VAR: value
              PATH_EXT: /usr/local/bin
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().NotContain(e => e.Field.StartsWith("environment."));
    }

    // --- Ports ---

    [Fact]
    public async Task InvalidPortRange_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            ports:
              0: http
              70000: other
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field.StartsWith("ports."));
    }

    // --- Dependencies ---

    [Fact]
    public async Task DuplicateDependencyNames_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - type: sdk
                name: dotnet
                version: "8.0.*"
              - type: runtime
                name: dotnet
                version: "9.0.*"
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Duplicate dependency"));
    }

    [Fact]
    public async Task DependencyMissingType_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - name: python
                version: "3.12.*"
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field.Contains("type") && e.Message.Contains("required"));
    }

    [Fact]
    public async Task DependencyInvalidVersionConstraint_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - type: sdk
                name: dotnet
                version: "8.0.*.1"
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Invalid version constraint"));
    }

    [Fact]
    public async Task ValidDependencies_NoError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            dependencies:
              - type: sdk
                name: dotnet
                version: "8.0.*"
              - type: runtime
                name: python
                version: ">=3.12,<4.0"
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Errors.Should().NotContain(e => e.Field.StartsWith("dependencies"));
    }

    // --- Tags ---

    [Fact]
    public async Task TooManyTags_ReturnsError()
    {
        var tagList = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"  - tag{i}"));
        var yaml = $"""
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            tags:
            {tagList}
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "tags" && e.Message.Contains("Maximum"));
    }

    // --- IDE type warning ---

    [Fact]
    public async Task CodeServerWithoutPort8080_GeneratesWarning()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            ide_type: code-server
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.Warnings.Should().Contain(w => w.Message.Contains("8080"));
    }

    // --- Invalid catalog_scope ---

    [Fact]
    public async Task InvalidCatalogScope_ReturnsError()
    {
        var yaml = """
            code: my-template
            name: Test
            version: 1.0.0
            base_image: ubuntu:24.04
            catalog_scope: invalid
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "catalog_scope");
    }

    // --- ParseYamlToTemplate ---

    [Fact]
    public async Task ParseYamlToTemplate_BasicTemplate_ParsesCorrectly()
    {
        var yaml = """
            code: dotnet-dev
            name: .NET Developer
            version: 2.1.0
            base_image: mcr.microsoft.com/dotnet/sdk:8.0
            description: Full .NET development environment
            catalog_scope: global
            ide_type: code-server
            tags:
              - dotnet
              - csharp
            """;

        var template = await _validator.ParseYamlToTemplateAsync(yaml);

        template.Code.Should().Be("dotnet-dev");
        template.Name.Should().Be(".NET Developer");
        template.Version.Should().Be("2.1.0");
        template.BaseImage.Should().Be("mcr.microsoft.com/dotnet/sdk:8.0");
        template.Description.Should().Be("Full .NET development environment");
        template.CatalogScope.Should().Be(CatalogScope.Global);
        template.IdeType.Should().Be(IdeType.CodeServer);
        template.Tags.Should().BeEquivalentTo(["dotnet", "csharp"]);
    }

    [Fact]
    public async Task ParseYamlToTemplate_WithGpu_ParsesCorrectly()
    {
        var yaml = """
            code: gpu-ml
            name: GPU ML
            version: 1.0.0
            base_image: nvidia/cuda:12.0-base
            gpu:
              required: true
              preferred: true
            """;

        var template = await _validator.ParseYamlToTemplateAsync(yaml);

        template.GpuRequired.Should().BeTrue();
        template.GpuPreferred.Should().BeTrue();
    }

    [Fact]
    public async Task DeeplyNestedYaml_ReturnsInvalid()
    {
        // Create YAML with >10 levels of nesting
        var yaml = """
            code: deep-test
            name: Deep
            version: 1.0.0
            base_image: ubuntu:24.04
            level1:
              level2:
                level3:
                  level4:
                    level5:
                      level6:
                        level7:
                          level8:
                            level9:
                              level10:
                                level11: too deep
            """;

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("nesting depth"));
    }

    [Fact]
    public async Task ExcessiveAnchors_ReturnsInvalid()
    {
        // Create YAML with many anchors
        var yaml = "code: anchor-test\nname: Anchors\nversion: 1.0.0\nbase_image: ubuntu:24.04\n";
        for (int i = 0; i < 12; i++)
            yaml += $"key{i}: &anchor{i} value{i}\n";

        var result = await _validator.ValidateYamlAsync(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("anchors/aliases"));
    }
}

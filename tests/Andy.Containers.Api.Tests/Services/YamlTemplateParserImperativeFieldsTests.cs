using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// IM4 (rivoli-ai/andy-containers#253). Coverage for the M1.9
// imperative-style fields (extends, from, packages, files, install,
// entrypoint, markers). Existing fixtures + the
// YamlTemplateParserTests above stay green — these tests add to the
// suite, they don't replace anything.
public class YamlTemplateParserImperativeFieldsTests
{
    private readonly YamlTemplateParser _parser = new();

    [Fact]
    public void Validate_AcceptsAllNewImperativeFields()
    {
        var yaml = """
            code: conductor-terminal-claude-code
            name: Conductor Terminal — Claude Code
            version: 1.0.0
            base_image: ubuntu:22.04
            packages:
              - curl
              - ca-certificates
            files:
              - source: install.sh
                dest: /opt/conductor/install.sh
                mode: 0755
            install:
              - npm install -g @anthropic-ai/claude-code
            entrypoint: /opt/conductor/entrypoint.sh
            markers:
              baked-assistants:
                - claude-code
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue(
            "all imperative fields are well-formed: " + string.Join(", ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PopulatesNewFieldsOnContainerTemplate()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            extends: parent-template
            packages: [curl]
            files:
              - source: a.sh
                dest: /a.sh
            install:
              - echo hi
            entrypoint: /entrypoint.sh
            markers:
              kind: test
            """;

        var template = _parser.Parse(yaml);

        template.Extends.Should().Be("parent-template");
        template.EntryPoint.Should().Be("/entrypoint.sh");
        template.Packages.Should().Contain("curl");
        template.Files.Should().Contain("a.sh").And.Contain("/a.sh");
        template.Install.Should().Contain("echo hi");
        template.Markers.Should().Contain("test");
    }

    // --- 'from:' deprecation ---

    [Fact]
    public void Validate_FromAlias_EmitsDeprecationWarning()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            from: ubuntu:22.04
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.Field == "from")
            .Which.Message.Should().Contain("deprecated");
    }

    [Fact]
    public void Parse_FromAlias_PopulatesBaseImage()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            from: ubuntu:22.04
            """;

        var template = _parser.Parse(yaml);

        template.BaseImage.Should().Be("ubuntu:22.04",
            "'from' is treated as an alias of base_image so downstream code only sees BaseImage.");
    }

    [Fact]
    public void Validate_BothBaseImageAndFrom_IsAmbiguousError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            from: alpine:3.20
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "from" && e.Message.Contains("ambiguous"));
    }

    // --- extends — base-image alternative ---

    [Fact]
    public void Validate_ExtendsWithoutBaseImage_IsValid()
    {
        // extends supplies the base transitively — no need for
        // base_image at parse time. Resolution is the registration
        // pipeline's job.
        var yaml = """
            code: child
            name: Child
            version: 1.0.0
            extends: parent
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_NoBaseImageOrExtendsOrFrom_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "base_image");
    }

    [Fact]
    public void Validate_ExtendsWithInvalidCode_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            extends: BAD CODE
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "extends");
    }

    // --- packages ---

    [Fact]
    public void Validate_PackagesNotAList_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            packages: not-a-list
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "packages");
    }

    [Fact]
    public void Validate_PackagesWithEmptyEntry_FlagsIndex()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            packages:
              - curl
              - ""
              - git
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "packages[1]",
            "field path makes it obvious which entry is wrong.");
    }

    // --- files ---

    [Fact]
    public void Validate_FilesEntryMissingSource_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            files:
              - dest: /opt/foo
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "files[0].source");
    }

    [Fact]
    public void Validate_FilesEntryMissingDest_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            files:
              - source: a.sh
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "files[0].dest");
    }

    [Fact]
    public void Validate_FilesEntryRelativeDest_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            files:
              - source: a.sh
                dest: not-absolute/a.sh
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "files[0].dest" && e.Message.Contains("absolute"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(420)]      // 0644 octal
    [InlineData(493)]      // 0755 octal
    [InlineData(4095)]     // 07777 octal
    public void Validate_FilesMode_AcceptsValidOctals(int mode)
    {
        var yaml = $$"""
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            files:
              - source: a.sh
                dest: /a.sh
                mode: {{mode}}
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().NotContain(e => e.Field == "files[0].mode");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8192)]    // > 07777
    public void Validate_FilesMode_RejectsOutOfRange(int mode)
    {
        var yaml = $$"""
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            files:
              - source: a.sh
                dest: /a.sh
                mode: {{mode}}
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "files[0].mode");
    }

    // --- install ---

    [Fact]
    public void Validate_InstallNotAList_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            install: echo hi
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "install");
    }

    // --- entrypoint ---

    [Fact]
    public void Validate_EntrypointAsList_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            entrypoint:
              - /bin/sh
              - -c
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "entrypoint" && e.Message.Contains("not yet supported"));
    }

    // --- markers ---

    [Fact]
    public void Validate_MarkersAsList_IsError()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            markers:
              - claude-code
            """;

        var result = _parser.Validate(yaml);

        result.Errors.Should().Contain(e => e.Field == "markers");
    }

    // --- Unknown-key warnings still fire for typos ---

    [Fact]
    public void Validate_UnknownTopLevelKey_StillEmitsWarning()
    {
        var yaml = """
            code: t1
            name: T1
            version: 1.0.0
            base_image: ubuntu:22.04
            misspelled_packages: [curl]
            """;

        var result = _parser.Validate(yaml);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Field == "misspelled_packages");
    }
}

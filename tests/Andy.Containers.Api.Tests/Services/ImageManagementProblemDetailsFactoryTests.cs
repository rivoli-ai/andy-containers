using Andy.Containers.Abstractions.Images;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Registries;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// IM10 (rivoli-ai/andy-containers#264). Reachability tests for every
// stable code in the catalogue + truncation behaviour for build logs.
// Adding a new error code without a test here means the API has a
// failure mode no client knows how to branch on.
public class ImageManagementProblemDetailsFactoryTests
{
    [Fact]
    public void FromBuildFailure_EngineDetect_Returns503()
    {
        var ex = new ImageBuildFailedException(
            backendId: "local",
            capturedLogs: "no engine on host",
            failingStepName: "engine-detect");

        var result = ImageManagementProblemDetailsFactory.FromBuildFailure(ex);

        AssertResult(result, StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.BuildEngineUnavailable);
    }

    [Fact]
    public void FromBuildFailure_GenericStep_Returns422WithLogs()
    {
        var ex = new ImageBuildFailedException(
            backendId: "local",
            capturedLogs: "Step 5/12 failed\nERROR Unable to install package",
            failingStepName: "build");

        var result = ImageManagementProblemDetailsFactory.FromBuildFailure(ex);

        AssertResult(result, StatusCodes.Status422UnprocessableEntity, ImageManagementErrors.BuildFailed);
        var body = ExtractBody(result);
        body.BuildLog.Should().Contain("Unable to install package");
    }

    [Fact]
    public void FromRegistryFailure_DockerLaunchFailed_Returns503()
    {
        var ex = new RegistryUploadException(
            code: "DockerCliUploader.Tag.LaunchFailed",
            message: "docker not on PATH");

        var result = ImageManagementProblemDetailsFactory.FromRegistryFailure(ex);

        AssertResult(result, StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.BuildEngineUnavailable);
    }

    [Fact]
    public void FromRegistryFailure_QuotaInMessage_Returns507()
    {
        var ex = new RegistryUploadException(
            code: "LocalZotAdapter.Push.PostHeadHttp507",
            message: "registry storage quota exceeded");

        var result = ImageManagementProblemDetailsFactory.FromRegistryFailure(ex);

        AssertResult(result, StatusCodes.Status507InsufficientStorage, ImageManagementErrors.RegistryQuotaExceeded);
    }

    [Theory]
    [InlineData(ImageManagementErrors.TemplateNotFound, StatusCodes.Status404NotFound, ImageManagementErrors.TemplateNotFound)]
    [InlineData(ImageManagementErrors.RegistryNotConfigured, StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.RegistryNotConfigured)]
    [InlineData("build.engine-detect", StatusCodes.Status503ServiceUnavailable, ImageManagementErrors.BuildEngineUnavailable)]
    [InlineData("registry.quota.exceeded", StatusCodes.Status507InsufficientStorage, ImageManagementErrors.RegistryQuotaExceeded)]
    [InlineData("build.failed", StatusCodes.Status422UnprocessableEntity, ImageManagementErrors.BuildFailed)]
    [InlineData("build.packages-install", StatusCodes.Status422UnprocessableEntity, ImageManagementErrors.BuildFailed)]
    public void FromOrchestratorErrorCode_MapsKnownCodes(string code, int expectedStatus, string expectedCode)
    {
        var result = ImageManagementProblemDetailsFactory.FromOrchestratorErrorCode(code, "msg", null);

        AssertResult(result, expectedStatus, expectedCode);
    }

    [Fact]
    public void FromValidationErrors_FlattensFirstError()
    {
        var validation = new YamlValidationResult
        {
            IsValid = false,
            Errors =
            [
                new YamlValidationError { Field = "files[0].dest", Message = "must be absolute" },
                new YamlValidationError { Field = "code", Message = "required" },
            ],
        };

        var result = ImageManagementProblemDetailsFactory.FromValidationErrors(validation);

        AssertResult(result, StatusCodes.Status400BadRequest, ImageManagementErrors.TemplateSpecInvalid);
        var body = ExtractBody(result);
        body.Field.Should().Be("files[0].dest",
            "the first error's field surfaces at the top level so a simple client can branch without descending into errors[].");
    }

    [Fact]
    public void FromCodeInUse_Returns409WithExistingTemplateId()
    {
        var existingId = Guid.NewGuid();

        var result = ImageManagementProblemDetailsFactory.FromCodeInUse("test-template", existingId);

        AssertResult(result, StatusCodes.Status409Conflict, ImageManagementErrors.TemplateCodeInUse);
        var body = ExtractBody(result);
        body.Field.Should().Be("code");
    }

    [Fact]
    public void NotFound_BuildsSpecifiedCode()
    {
        var result = ImageManagementProblemDetailsFactory.NotFound(
            ImageManagementErrors.BuildNotFound,
            "no build with that id");

        AssertResult(result, StatusCodes.Status404NotFound, ImageManagementErrors.BuildNotFound);
    }

    // --- Truncation ---

    [Fact]
    public void TruncateLog_ReturnsNullForNullInput()
    {
        ImageManagementProblemDetailsFactory.TruncateLog(null, 1024).Should().BeNull();
    }

    [Fact]
    public void TruncateLog_ReturnsInputUnderCap()
    {
        var log = "short log";
        ImageManagementProblemDetailsFactory.TruncateLog(log, 1024).Should().Be(log);
    }

    [Fact]
    public void TruncateLog_AppendsMarkerOverCap()
    {
        var log = new string('x', 100);
        var truncated = ImageManagementProblemDetailsFactory.TruncateLog(log, 50);

        truncated.Should().NotBeNull();
        truncated!.Should().EndWith("[truncated]");
        System.Text.Encoding.UTF8.GetByteCount(truncated!).Should().BeLessOrEqualTo(50,
            "the cap must be honoured even after the suffix is appended.");
    }

    [Fact]
    public void TruncateLog_HonoursCapWithMultiByteCharacters()
    {
        // "ñ" is two UTF-8 bytes. Each repetition costs 2 bytes; the
        // truncator must not split a codepoint mid-byte even when
        // forced to truncate.
        var log = string.Concat(Enumerable.Repeat("ñ", 100));
        var truncated = ImageManagementProblemDetailsFactory.TruncateLog(log, 50);

        truncated.Should().NotBeNull();
        // The truncated output must round-trip through UTF-8 without
        // raising a codepoint-decode error — the simplest way to
        // assert no mid-codepoint cut is to re-encode and decode.
        var bytes = System.Text.Encoding.UTF8.GetBytes(truncated!);
        var decoded = System.Text.Encoding.UTF8.GetString(bytes);
        decoded.Should().Be(truncated);
    }

    private static void AssertResult(ObjectResult result, int expectedStatus, string expectedCode)
    {
        result.StatusCode.Should().Be(expectedStatus);
        var body = ExtractBody(result);
        body.Code.Should().Be(expectedCode);
    }

    private static ImageManagementErrorBody ExtractBody(ObjectResult result)
        => result.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
}

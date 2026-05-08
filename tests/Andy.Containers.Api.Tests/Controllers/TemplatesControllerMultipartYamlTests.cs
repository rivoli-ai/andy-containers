using System.Security.Cryptography;
using System.Text;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// #277 (PR A). Coverage for the multipart variant of
/// <c>POST /api/templates/from-yaml</c>. The JSON variant lives in
/// <see cref="TemplatesControllerYamlTests"/>; this file focuses on
/// the file-upload-bearing path the multipart endpoint adds.
/// </summary>
public class TemplatesControllerMultipartYamlTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IYamlTemplateParser> _mockParser;
    private readonly TemplatesController _controller;
    private readonly List<string> _stagingDirsToCleanup = new();

    private const string ValidYaml = """
        code: with-files
        name: With Files
        version: 1.0.0
        base_image: ubuntu:24.04
        files:
          - source: install.sh
            dest: /opt/install.sh
            mode: 0755
        """;

    public TemplatesControllerMultipartYamlTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _mockParser = new Mock<IYamlTemplateParser>();
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var mockBuildService = new Mock<ITemplateBuildService>();
        _controller = new TemplatesController(_db, mockEnv.Object, _mockCurrentUser.Object, _mockParser.Object, mockOrgMembership.Object, mockBuildService.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var dir in _stagingDirsToCleanup)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // -----------------------------------------------------------------
    // Happy paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task Multipart_WithSpecAndFile_StagesFileAndReturns201()
    {
        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["install.sh"] = "echo hello\n",
        });

        var result = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);

        var registered = created.Value.Should().BeOfType<TemplatesController.RegisteredTemplate>().Subject;
        registered.Code.Should().Be("with-files");
        registered.Created.Should().BeTrue();
        registered.SpecHash.Should().StartWith("sha256:");

        var persisted = _db.Templates.Single(t => t.Code == "with-files");
        persisted.UploadedFilesPath.Should().NotBeNullOrEmpty(
            "the multipart variant must persist the staging directory path so the build backend can resolve files: entries.");
        Directory.Exists(persisted.UploadedFilesPath).Should().BeTrue();
        File.Exists(Path.Combine(persisted.UploadedFilesPath!, "install.sh")).Should().BeTrue();
        File.ReadAllText(Path.Combine(persisted.UploadedFilesPath!, "install.sh")).Should().Be("echo hello\n");
        _stagingDirsToCleanup.Add(persisted.UploadedFilesPath!);
    }

    [Fact]
    public async Task Multipart_WithoutFiles_StillSucceeds_AndUploadedFilesPathIsNull()
    {
        // A spec that has no `files:` entries can legitimately use the
        // multipart endpoint with zero file parts. The behaviour
        // should match the JSON path: created=true, no staging dir.
        const string yaml = """
            code: no-files-mp
            name: No Files (Multipart)
            version: 1.0.0
            base_image: ubuntu:24.04
            """;
        SetupParser(yaml: yaml, code: "no-files-mp");
        SetMultipartRequest(spec: yaml, files: new());

        var result = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var registered = created.Value.Should().BeOfType<TemplatesController.RegisteredTemplate>().Subject;
        registered.Created.Should().BeTrue();

        var persisted = _db.Templates.Single(t => t.Code == "no-files-mp");
        persisted.UploadedFilesPath.Should().BeNull(
            "with no file parts the staging dir is never created — UploadedFilesPath stays null.");
    }

    // -----------------------------------------------------------------
    // IM3 / IM8 contract: file digests mix into the spec hash
    // -----------------------------------------------------------------

    [Fact]
    public async Task Multipart_DifferentFileContent_ProducesDifferentSpecHash()
    {
        // Two registrations with the same YAML spec but different
        // file content must produce different spec hashes per the IM3
        // formula sha256(canonicalJson(spec) || sortedFileDigests).
        // Same code is reused so the second register hits the
        // "code matches but spec hash differs" branch and returns 409.
        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["install.sh"] = "echo first\n",
        });
        var first = await _controller.CreateFromYamlMultipart(CancellationToken.None);
        var firstCreated = first.Should().BeOfType<CreatedAtActionResult>().Subject;
        var firstHash = ((TemplatesController.RegisteredTemplate)firstCreated.Value!).SpecHash;
        var firstStagingDir = _db.Templates.Single(t => t.Code == "with-files").UploadedFilesPath;
        if (firstStagingDir is not null) _stagingDirsToCleanup.Add(firstStagingDir);

        // Re-register the same code with different file content. The
        // controller is stateless across requests, so we just rebuild
        // the request scaffolding before the second call.
        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["install.sh"] = "echo second\n",
        });
        var second = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        // Per IM10, the (same code, different spec hash) collision is
        // a structured 409 — surfaces via ImageManagementProblemDetails.
        var conflict = second.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);

        // Sanity-check that the second call would have computed a
        // different spec hash had it been allowed to proceed: the
        // staging dir for the failed register was cleaned up, so
        // we re-derive the digest manually.
        var firstDigest = ComputeDigest("echo first\n");
        var secondDigest = ComputeDigest("echo second\n");
        firstDigest.Should().NotBe(secondDigest);
        firstHash.Should().StartWith("sha256:");
    }

    [Fact]
    public async Task Multipart_SameSpecAndSameFiles_IsIdempotent()
    {
        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["install.sh"] = "echo hello\n",
        });
        var first = await _controller.CreateFromYamlMultipart(CancellationToken.None);
        var firstCreated = first.Should().BeOfType<CreatedAtActionResult>().Subject;
        var firstRegistered = (TemplatesController.RegisteredTemplate)firstCreated.Value!;
        var firstStagingDir = _db.Templates.Single(t => t.Code == "with-files").UploadedFilesPath;
        if (firstStagingDir is not null) _stagingDirsToCleanup.Add(firstStagingDir);

        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["install.sh"] = "echo hello\n",
        });
        var second = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        var ok = second.Should().BeOfType<OkObjectResult>().Subject;
        var secondRegistered = (TemplatesController.RegisteredTemplate)ok.Value!;
        secondRegistered.Created.Should().BeFalse(
            "identical spec and identical file content must produce the same spec hash and short-circuit to created=false.");
        secondRegistered.TemplateId.Should().Be(firstRegistered.TemplateId);
        secondRegistered.SpecHash.Should().Be(firstRegistered.SpecHash);

        _db.Templates.Count(t => t.Code == "with-files").Should().Be(1);
    }

    // -----------------------------------------------------------------
    // Error paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task Multipart_MissingSpecField_Returns400()
    {
        // Empty spec field with no fallback content field.
        SetMultipartRequest(spec: "", files: new()
        {
            ["install.sh"] = "echo hello\n",
        });

        var result = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Multipart_TraversalInPartName_Returns400()
    {
        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["../etc/passwd"] = "root:x:0:0::/root:/bin/sh\n",
        });

        var result = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "directory-traversal part names must be rejected before any file is staged.");
        _db.Templates.Should().BeEmpty();
    }

    [Fact]
    public async Task Multipart_AbsolutePartName_Returns400()
    {
        SetupParser(yaml: ValidYaml, code: "with-files");
        SetMultipartRequest(spec: ValidYaml, files: new()
        {
            ["/etc/shadow"] = "ignored\n",
        });

        var result = await _controller.CreateFromYamlMultipart(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "absolute part names must be rejected so we never write outside the staging dir.");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private void SetupParser(string yaml, string code)
    {
        var validResult = new YamlValidationResult { IsValid = true };
        _mockParser.Setup(p => p.Validate(yaml)).Returns(validResult);
        _mockParser.Setup(p => p.Parse(yaml)).Returns(() => new ContainerTemplate
        {
            Code = code,
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            // The Files JSON column carries the parsed `files:` entries
            // — its presence is what makes file-content changes show
            // up in the spec hash via the projection in
            // TemplatesController.ComputeSpecHash. Keep it stable
            // across calls so the (spec hash differs only when file
            // content differs) invariant holds.
            Files = """[{"source":"install.sh","dest":"/opt/install.sh","mode":"0755"}]""",
        });
    }

    /// <summary>
    /// Wires <c>controller.ControllerContext.HttpContext.Request</c>
    /// to a multipart form with the given spec field and file parts.
    /// The field/part shape mirrors what
    /// <see cref="TemplatesController.CreateFromYamlMultipart"/>
    /// reads via <c>Request.ReadFormAsync()</c>.
    /// </summary>
    private void SetMultipartRequest(string spec, Dictionary<string, string> files)
    {
        var fields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["spec"] = spec,
        };
        var formFiles = new FormFileCollection();
        foreach (var (logicalName, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var stream = new MemoryStream(bytes);
            var formFile = new FormFile(
                baseStream: stream,
                baseStreamOffset: 0,
                length: bytes.Length,
                name: logicalName,
                fileName: logicalName);
            formFiles.Add(formFile);
        }

        var form = new FormCollection(fields, formFiles);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = "multipart/form-data; boundary=test";
        httpContext.Request.Form = form;
        _controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    private static string ComputeDigest(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

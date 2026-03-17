using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Grpc;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Grpc.Core;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerGrpcServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ITemplateValidator> _mockValidator;
    private readonly ContainerGrpcService _service;

    public ContainerGrpcServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockValidator = new Mock<ITemplateValidator>();
        _service = new ContainerGrpcService(_db, _mockValidator.Object);
    }

    public void Dispose() => _db.Dispose();

    private static ServerCallContext MockContext() =>
        new MockServerCallContext();

    [Fact]
    public async Task ValidateTemplateYaml_ValidYaml_ShouldReturnValid()
    {
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });

        var response = await _service.ValidateTemplateYaml(
            new ValidateTemplateYamlRequest { YamlContent = "code: test\nname: Test\nversion: 1.0.0\nbase_image: ubuntu:24.04" },
            MockContext());

        response.IsValid.Should().BeTrue();
        response.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateTemplateYaml_InvalidYaml_ShouldReturnErrors()
    {
        var result = new TemplateValidationResult { Valid = false };
        result.Errors.Add(new TemplateValidationError { Field = "code", Message = "Required" });
        result.Warnings.Add(new TemplateValidationWarning { Field = "ports", Message = "Port 8080 missing" });
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var response = await _service.ValidateTemplateYaml(
            new ValidateTemplateYamlRequest { YamlContent = "name: test" },
            MockContext());

        response.IsValid.Should().BeFalse();
        response.Errors.Should().HaveCount(1);
        response.Errors[0].Field.Should().Be("code");
        response.Warnings.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateTemplateFromYaml_ValidYaml_ShouldCreateTemplate()
    {
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerTemplate
            {
                Code = "grpc-created",
                Name = "gRPC Created",
                Version = "1.0.0",
                BaseImage = "ubuntu:24.04"
            });

        var response = await _service.CreateTemplateFromYaml(
            new CreateTemplateFromYamlRequest { YamlContent = "code: grpc-created" },
            MockContext());

        response.Code.Should().Be("grpc-created");
        response.Name.Should().Be("gRPC Created");
        Guid.TryParse(response.Id, out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateTemplateFromYaml_InvalidYaml_ShouldThrowRpcException()
    {
        var result = new TemplateValidationResult { Valid = false };
        result.Errors.Add(new TemplateValidationError { Field = "code", Message = "Required" });
        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var act = () => _service.CreateTemplateFromYaml(
            new CreateTemplateFromYamlRequest { YamlContent = "name: test" },
            MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task UpdateTemplateDefinition_ValidYaml_ShouldUpdateFields()
    {
        var template = new ContainerTemplate
        {
            Code = "grpc-update",
            Name = "Original",
            Version = "1.0.0",
            BaseImage = "ubuntu:22.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        _mockValidator.Setup(v => v.ValidateYamlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateValidationResult { Valid = true });
        _mockValidator.Setup(v => v.ParseYamlToTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerTemplate
            {
                Code = "grpc-update",
                Name = "Updated",
                Version = "2.0.0",
                BaseImage = "ubuntu:24.04"
            });

        var response = await _service.UpdateTemplateDefinition(
            new UpdateTemplateDefinitionRequest { TemplateId = template.Id.ToString(), YamlContent = "code: grpc-update" },
            MockContext());

        response.Name.Should().Be("Updated");
        response.Version.Should().Be("2.0.0");
        response.BaseImage.Should().Be("ubuntu:24.04");
    }

    [Fact]
    public async Task UpdateTemplateDefinition_NonExistent_ShouldThrowNotFound()
    {
        var act = () => _service.UpdateTemplateDefinition(
            new UpdateTemplateDefinitionRequest { TemplateId = Guid.NewGuid().ToString(), YamlContent = "code: test" },
            MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task GetTemplate_ById_ShouldReturnTemplate()
    {
        var template = new ContainerTemplate
        {
            Code = "grpc-get",
            Name = "gRPC Get",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            Tags = ["dotnet", "test"]
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var response = await _service.GetTemplate(
            new GetTemplateRequest { TemplateId = template.Id.ToString() },
            MockContext());

        response.Code.Should().Be("grpc-get");
        response.Name.Should().Be("gRPC Get");
        response.Tags.Should().Contain("dotnet");
    }

    [Fact]
    public async Task GetTemplate_ByCode_ShouldReturnTemplate()
    {
        _db.Templates.Add(new ContainerTemplate
        {
            Code = "grpc-code",
            Name = "By Code",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        });
        await _db.SaveChangesAsync();

        var response = await _service.GetTemplate(
            new GetTemplateRequest { TemplateCode = "grpc-code" },
            MockContext());

        response.Code.Should().Be("grpc-code");
    }

    [Fact]
    public async Task GetTemplate_NonExistent_ShouldThrowNotFound()
    {
        var act = () => _service.GetTemplate(
            new GetTemplateRequest { TemplateId = Guid.NewGuid().ToString() },
            MockContext());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task ListTemplates_ShouldReturnPublishedOnly()
    {
        _db.Templates.AddRange(
            new ContainerTemplate { Code = "grpc-pub", Name = "Published", Version = "1.0", BaseImage = "img", IsPublished = true },
            new ContainerTemplate { Code = "grpc-unpub", Name = "Unpub", Version = "1.0", BaseImage = "img", IsPublished = false }
        );
        await _db.SaveChangesAsync();

        var response = await _service.ListTemplates(
            new ListTemplatesRequest { Take = 20 },
            MockContext());

        response.Templates.Should().HaveCount(1);
        response.Templates[0].Code.Should().Be("grpc-pub");
        response.TotalCount.Should().Be(1);
    }
}

/// <summary>Minimal mock for ServerCallContext used in gRPC unit tests.</summary>
internal class MockServerCallContext : ServerCallContext
{
    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "test-peer";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => [];
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new("test", new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotImplementedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
        Task.CompletedTask;
}

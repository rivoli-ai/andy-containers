using Andy.Containers.Client;
using Andy.Containers.Models;
using FluentAssertions;
using System.Net;
using System.Text;
using Xunit;

namespace Andy.Containers.Client.Tests;

public class ContainersClientTests
{
    [Fact]
    public void ContainersApiException_ContainsStatusCodeAndBody()
    {
        var ex = new ContainersApiException(HttpStatusCode.Forbidden, "Access denied");

        ex.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ex.ResponseBody.Should().Be("Access denied");
        ex.Message.Should().Contain("403");
        ex.Message.Should().Contain("Access denied");
    }

    [Fact]
    public void ContainersApiException_HandlesNullBody()
    {
        var ex = new ContainersApiException(HttpStatusCode.InternalServerError, null);

        ex.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ex.ResponseBody.Should().BeNull();
        ex.Message.Should().Contain("500");
    }

    [Fact]
    public void ContainerDto_RecordEquality()
    {
        var dto1 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1", null, null, null, null, null, null, null, null, null, null, null);
        var dto2 = new ContainersClient.ContainerDto("id1", "test", "Running", null, "user1", null, null, null, null, null, null, null, null, null, null, null);

        dto1.Should().Be(dto2);
    }

    [Fact]
    public void PaginatedResult_StoresItemsAndCount()
    {
        var items = new[]
        {
            new ContainersClient.ContainerDto("1", "c1", "Running", null, "u1", null, null, null, null, null, null, null, null, null, null, null)
        };
        var result = new ContainersClient.PaginatedResult<ContainersClient.ContainerDto>(items, 1);

        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
    }

    // AP9 (rivoli-ai/andy-containers#111). The CLI's `runs events` command
    // depends on this NDJSON parser handling chunked, line-delimited JSON
    // gracefully. We feed canned bytes through a fake HttpMessageHandler
    // and assert the IAsyncEnumerable yields one DTO per line.

    [Fact]
    public async Task StreamRunEventsAsync_ParsesNdjson_YieldsDtoPerLine()
    {
        var runId = Guid.NewGuid();
        var ndjson =
            $"{{\"run_id\":\"{runId}\",\"subject\":\"andy.containers.events.run.{runId}.finished\"," +
            "\"kind\":\"finished\",\"status\":\"Succeeded\",\"exit_code\":0,\"duration_seconds\":1.5," +
            "\"timestamp\":\"2026-04-27T12:00:00+00:00\",\"correlation_id\":\"00000000-0000-0000-0000-000000000001\"}\n" +
            $"{{\"run_id\":\"{runId}\",\"subject\":\"andy.containers.events.run.{runId}.cancelled\"," +
            "\"kind\":\"cancelled\",\"status\":\"Cancelled\",\"exit_code\":null,\"duration_seconds\":null," +
            "\"timestamp\":\"2026-04-27T12:00:01+00:00\",\"correlation_id\":\"00000000-0000-0000-0000-000000000001\"}\n";

        var http = new HttpClient(new CannedHandler(ndjson, "application/x-ndjson"))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var events = new List<RunEventDto>();
        await foreach (var evt in client.StreamRunEventsAsync(runId.ToString()))
        {
            events.Add(evt);
        }

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be("finished");
        events[0].ExitCode.Should().Be(0);
        events[1].Kind.Should().Be("cancelled");
        events[1].ExitCode.Should().BeNull();
    }

    [Fact]
    public async Task StreamRunEventsAsync_EmptyBody_YieldsNothing()
    {
        var http = new HttpClient(new CannedHandler("", "application/x-ndjson"))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var events = new List<RunEventDto>();
        await foreach (var evt in client.StreamRunEventsAsync(Guid.NewGuid().ToString()))
        {
            events.Add(evt);
        }

        events.Should().BeEmpty(
            "an immediate-close NDJSON response (e.g. terminal run with no backfill) is valid");
    }

    [Fact]
    public async Task StreamRunEventsAsync_MalformedLine_SkipsAndContinues()
    {
        var runId = Guid.NewGuid();
        var ndjson =
            "{not-valid-json\n" +
            $"{{\"run_id\":\"{runId}\",\"subject\":\"andy.containers.events.run.{runId}.finished\"," +
            "\"kind\":\"finished\",\"status\":\"Succeeded\",\"exit_code\":0,\"duration_seconds\":1.0," +
            "\"timestamp\":\"2026-04-27T12:00:00+00:00\",\"correlation_id\":\"00000000-0000-0000-0000-000000000001\"}\n";

        var http = new HttpClient(new CannedHandler(ndjson, "application/x-ndjson"))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var events = new List<RunEventDto>();
        await foreach (var evt in client.StreamRunEventsAsync(runId.ToString()))
        {
            events.Add(evt);
        }

        events.Should().ContainSingle(
            "the well-formed line after the malformed one must still surface");
    }

    [Fact]
    public async Task StreamRunEventsAsync_404Response_ThrowsContainersApiException()
    {
        var http = new HttpClient(new CannedHandler("not found", "text/plain", HttpStatusCode.NotFound))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var act = async () =>
        {
            await foreach (var _ in client.StreamRunEventsAsync(Guid.NewGuid().ToString()))
            {
            }
        };

        await act.Should().ThrowAsync<ContainersApiException>()
            .Where(e => e.StatusCode == HttpStatusCode.NotFound);
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;
        private readonly HttpStatusCode _status;

        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        public CannedHandler(string body, string contentType, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = Encoding.UTF8.GetBytes(body);
            _contentType = contentType;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }

    // X7 (rivoli-ai/andy-containers#97). Environment catalog wrappers.
    // Pin the URL shape so a typo in the route or a regression in
    // pagination encoding becomes a test failure here, not a 404 in
    // the CLI.

    [Fact]
    public async Task ListEnvironmentsAsync_NoFilters_HitsBareCollectionUrl()
    {
        var handler = new CannedHandler(
            """{"items":[],"totalCount":0}""", "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var page = await client.ListEnvironmentsAsync();

        page.TotalCount.Should().Be(0);
        page.Items.Should().BeEmpty();
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/environments");
        handler.LastRequestUri.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task ListEnvironmentsAsync_AllFilters_AppendsQueryParams()
    {
        var handler = new CannedHandler(
            """{"items":[],"totalCount":0}""", "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        await client.ListEnvironmentsAsync(kind: "Headless Container", skip: 0, take: 25);

        var query = handler.LastRequestUri!.Query;
        query.Should().Contain("kind=Headless%20Container",
            "spaces in user-supplied kind values must be URL-encoded, not silently dropped");
        query.Should().Contain("skip=0");
        query.Should().Contain("take=25");
    }

    [Fact]
    public async Task ListEnvironmentsAsync_DeserialisesItems_AndCapabilities()
    {
        var profileId = Guid.NewGuid();
        var body = $$"""
            {
              "items": [{
                "id": "{{profileId}}",
                "code": "headless-container",
                "displayName": "Headless container",
                "kind": "HeadlessContainer",
                "baseImageRef": "ghcr.io/rivoli-ai/andy-headless:latest",
                "capabilities": {
                  "networkAllowlist": ["registry.rivoli.ai"],
                  "secretsScope": "WorkspaceScoped",
                  "hasGui": false,
                  "auditMode": "Strict"
                },
                "createdAt": "2026-04-27T00:00:00+00:00"
              }],
              "totalCount": 1
            }
            """;
        var http = new HttpClient(new CannedHandler(body, "application/json"))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var page = await client.ListEnvironmentsAsync();

        page.Items.Should().HaveCount(1);
        var dto = page.Items[0];
        dto.Code.Should().Be("headless-container");
        dto.Kind.Should().Be("HeadlessContainer");
        dto.Capabilities.HasGui.Should().BeFalse();
        dto.Capabilities.SecretsScope.Should()
            .Be(Andy.Containers.Models.SecretsScope.WorkspaceScoped);
        dto.Capabilities.AuditMode.Should()
            .Be(Andy.Containers.Models.AuditMode.Strict);
    }

    [Fact]
    public async Task GetEnvironmentByCodeAsync_HitsByCodeRoute_AndDeserialises()
    {
        var body = """
            {
              "id": "00000000-0000-0000-0000-000000000001",
              "code": "desktop",
              "displayName": "Desktop",
              "kind": "Desktop",
              "baseImageRef": "ghcr.io/rivoli-ai/andy-desktop:latest",
              "capabilities": {
                "networkAllowlist": ["*"],
                "secretsScope": "OrganizationScoped",
                "hasGui": true,
                "auditMode": "Standard"
              },
              "createdAt": "2026-04-27T00:00:00+00:00"
            }
            """;
        var handler = new CannedHandler(body, "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var dto = await client.GetEnvironmentByCodeAsync("desktop");

        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/environments/by-code/desktop");
        dto.Capabilities.HasGui.Should().BeTrue();
    }

    [Fact]
    public async Task GetEnvironmentByCodeAsync_404_ThrowsContainersApiException()
    {
        var http = new HttpClient(
            new CannedHandler("", "application/json", HttpStatusCode.NotFound))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var act = async () => await client.GetEnvironmentByCodeAsync("nope");

        await act.Should().ThrowAsync<ContainersApiException>()
            .Where(e => e.StatusCode == HttpStatusCode.NotFound);
    }

    // rivoli-ai/andy-containers#189. Workspace client wrappers. Same
    // pattern as the environment cases above: pin URL shapes and
    // round-tripping so a future regression breaks here, not in the
    // CLI integration.

    [Fact]
    public async Task ListWorkspacesAsync_NoFilters_HitsBareCollectionUrl()
    {
        var handler = new CannedHandler(
            """{"items":[],"totalCount":0}""", "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var page = await client.ListWorkspacesAsync();

        page.TotalCount.Should().Be(0);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/workspaces");
        handler.LastRequestUri.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWorkspacesAsync_WithFilters_AppendsQueryParams()
    {
        var handler = new CannedHandler(
            """{"items":[],"totalCount":0}""", "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var orgId = Guid.NewGuid();
        await client.ListWorkspacesAsync(ownerId: "alice user", organizationId: orgId, take: 50);

        var query = handler.LastRequestUri!.Query;
        query.Should().Contain("ownerId=alice%20user", "spaces in user-supplied owner must URL-encode");
        query.Should().Contain($"organizationId={orgId}");
        query.Should().Contain("take=50");
    }

    [Fact]
    public async Task GetWorkspaceAsync_HitsByIdRoute_AndDeserialises()
    {
        var id = Guid.NewGuid();
        var body = $$"""
            {
              "id": "{{id}}",
              "name": "demo",
              "description": "test ws",
              "ownerId": "alice",
              "organizationId": null,
              "teamId": null,
              "status": "Active",
              "defaultContainerId": null,
              "gitRepositoryUrl": null,
              "gitBranch": null,
              "environmentProfileId": "11111111-2222-3333-4444-555555555555",
              "createdAt": "2026-04-28T00:00:00Z",
              "updatedAt": null,
              "lastAccessedAt": null
            }
            """;
        var handler = new CannedHandler(body, "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var ws = await client.GetWorkspaceAsync(id.ToString());

        handler.LastRequestUri!.AbsolutePath.Should().Be($"/api/workspaces/{id}");
        ws.Name.Should().Be("demo");
        ws.Status.Should().Be("Active");
        ws.EnvironmentProfileId.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateWorkspaceAsync_PostsRequest_AndReturnsDto()
    {
        var newId = Guid.NewGuid();
        var responseBody = $$"""
            {
              "id": "{{newId}}",
              "name": "new",
              "description": null,
              "ownerId": "alice",
              "organizationId": null,
              "teamId": null,
              "status": "Active",
              "defaultContainerId": null,
              "gitRepositoryUrl": null,
              "gitBranch": null,
              "environmentProfileId": null,
              "createdAt": "2026-04-28T00:00:00Z",
              "updatedAt": null,
              "lastAccessedAt": null
            }
            """;
        var handler = new CannedHandler(responseBody, "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var request = new ContainersClient.CreateWorkspaceRequest(
            "new", null, null, null, null, null, "headless-container");
        var ws = await client.CreateWorkspaceAsync(request);

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/workspaces");
        ws.Id.Should().Be(newId);
    }

    [Fact]
    public async Task DeleteWorkspaceAsync_HitsByIdRoute()
    {
        var id = Guid.NewGuid();
        var handler = new CannedHandler("", "application/json", HttpStatusCode.NoContent);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        await client.DeleteWorkspaceAsync(id.ToString());

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri!.AbsolutePath.Should().Be($"/api/workspaces/{id}");
    }

    [Fact]
    public async Task DeleteWorkspaceAsync_404_ThrowsContainersApiException()
    {
        var handler = new CannedHandler("", "application/json", HttpStatusCode.NotFound);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var act = async () => await client.DeleteWorkspaceAsync(Guid.NewGuid().ToString());

        await act.Should().ThrowAsync<ContainersApiException>()
            .Where(e => e.StatusCode == HttpStatusCode.NotFound);
    }

    // rivoli-ai/andy-containers#190. Template client wrappers.

    [Fact]
    public async Task ListTemplatesAsync_NoFilters_HitsBareCollectionUrl()
    {
        var handler = new CannedHandler(
            """{"items":[],"totalCount":0}""", "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var page = await client.ListTemplatesAsync();

        page.TotalCount.Should().Be(0);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/templates");
        handler.LastRequestUri.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTemplatesAsync_WithFilters_AppendsQueryParams()
    {
        var handler = new CannedHandler(
            """{"items":[],"totalCount":0}""", "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        await client.ListTemplatesAsync(scope: "Global", search: "andy cli", take: 50);

        var query = handler.LastRequestUri!.Query;
        query.Should().Contain("scope=Global");
        query.Should().Contain("search=andy%20cli", "spaces in --search must URL-encode");
        query.Should().Contain("take=50");
    }

    [Fact]
    public async Task GetTemplateByCodeAsync_HitsByCodeRoute_AndDeserialises()
    {
        var id = Guid.NewGuid();
        var body = $$"""
            {
              "id": "{{id}}",
              "code": "andy-cli-dev",
              "name": "Andy CLI Development",
              "description": "Pre-installed CLI",
              "version": "1.0.0",
              "baseImage": "ubuntu:24.04",
              "catalogScope": "Global",
              "ideType": "CodeServer",
              "gpuRequired": false,
              "gpuPreferred": false,
              "isPublished": true,
              "organizationId": null,
              "teamId": null,
              "tags": null,
              "createdAt": "2026-04-28T00:00:00Z",
              "updatedAt": null
            }
            """;
        var handler = new CannedHandler(body, "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var t = await client.GetTemplateByCodeAsync("andy-cli-dev");

        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/templates/by-code/andy-cli-dev");
        t.Code.Should().Be("andy-cli-dev");
        t.CatalogScope.Should().Be("Global");
        t.IsPublished.Should().BeTrue();
    }

    [Fact]
    public async Task GetTemplateDefinitionAsync_HitsDefinitionRoute_AndUnpacksContentEnvelope()
    {
        var id = Guid.NewGuid();
        var body = """{"code":"andy-cli-dev","content":"code: andy-cli-dev\nname: Demo\n"}""";
        var handler = new CannedHandler(body, "application/json");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.local/") };
        var client = new ContainersClient(http);

        var def = await client.GetTemplateDefinitionAsync(id.ToString());

        handler.LastRequestUri!.AbsolutePath.Should().Be($"/api/templates/{id}/definition");
        def.Code.Should().Be("andy-cli-dev");
        def.Content.Should().Contain("name: Demo");
    }

    [Fact]
    public async Task GetTemplateByCodeAsync_404_ThrowsContainersApiException()
    {
        var http = new HttpClient(
            new CannedHandler("", "application/json", HttpStatusCode.NotFound))
        {
            BaseAddress = new Uri("https://example.local/"),
        };
        var client = new ContainersClient(http);

        var act = async () => await client.GetTemplateByCodeAsync("nope");

        await act.Should().ThrowAsync<ContainersApiException>()
            .Where(e => e.StatusCode == HttpStatusCode.NotFound);
    }
}

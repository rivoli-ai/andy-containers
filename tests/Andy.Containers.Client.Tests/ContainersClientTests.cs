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

        public CannedHandler(string body, string contentType, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = Encoding.UTF8.GetBytes(body);
            _contentType = contentType;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }
}

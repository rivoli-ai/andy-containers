using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Containers.Web.Tests.Helpers;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _handlers = new();
    private readonly List<HttpRequestMessage> _requests = [];

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    public void SetupGet<T>(string urlContains, T responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(responseBody, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        };
    }

    public void SetupPost<T>(string urlContains, T responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(responseBody, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        };
    }

    public void SetupPost(string urlContains, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
    }

    public void SetupPut<T>(string urlContains, T responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(responseBody, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        };
    }

    public void SetupPut(string urlContains, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
    }

    public void SetupDelete(string urlContains, HttpStatusCode statusCode = HttpStatusCode.NoContent)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode);
    }

    public void SetupError(string urlContains, HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
    {
        _handlers[urlContains] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("Error", System.Text.Encoding.UTF8, "text/plain")
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        var url = request.RequestUri?.ToString() ?? "";

        // Match the longest (most specific) key first
        var match = _handlers
            .Where(kvp => url.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Key.Length)
            .FirstOrDefault();

        if (match.Value is not null)
        {
            return Task.FromResult(match.Value(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No handler for {url}")
        });
    }

    public HttpClient CreateClient(string baseAddress = "https://localhost/")
    {
        return new HttpClient(this) { BaseAddress = new Uri(baseAddress) };
    }
}

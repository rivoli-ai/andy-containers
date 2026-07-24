using System.Text.Json;
using Andy.Containers.Abstractions;
using Microsoft.AspNetCore.Http.Features;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Writes one container exec as an SSE stream. Each stdout/stderr callback is
/// awaited so the provider cannot outrun a slow HTTP client indefinitely.
/// </summary>
public static class ContainerExecSse
{
    public static async Task StreamAsync(
        HttpResponse response,
        IContainerService containerService,
        Guid containerId,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            var result = await containerService.ExecStreamingAsync(
                containerId,
                command,
                timeout,
                async (chunk, writeCt) =>
                {
                    var eventName = chunk.Stream == ExecStreamKind.Stderr
                        ? "stderr"
                        : "stdout";
                    await WriteEventAsync(
                        response,
                        eventName,
                        new ExecLineEvent(chunk.Line),
                        writeCt);
                },
                ct);

            await WriteEventAsync(
                response,
                "done",
                new ExecDoneEvent(result.ExitCode),
                ct);
        }
        catch (OperationCanceledException) when (
            ct.IsCancellationRequested ||
            response.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // A disconnected SSE client cancels RequestAborted. The same
            // token is passed through the orchestration and provider layers,
            // terminating the attached exec instead of leaving this request
            // draining output nobody can consume.
        }
    }

    private static async ValueTask WriteEventAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, SseJsonOptions);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private sealed record ExecLineEvent(string Line);
    private sealed record ExecDoneEvent(int ExitCode);

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

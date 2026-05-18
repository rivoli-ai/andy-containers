// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Audit;

/// <summary>
/// rivoli-ai/andy-containers#320. Tiny DelegatingHandler that resolves a
/// bearer token (typically from an M2M client-credentials cache) and
/// attaches it to every outbound request as
/// <c>Authorization: Bearer &lt;token&gt;</c>. Registered as the message
/// handler on the named <c>andy-docs-artifacts</c> HttpClient when
/// AndyAuth is wired; in bypass mode the handler is omitted entirely
/// and the client goes anonymous.
///
/// <para>
/// Failures from the token provider degrade to "send without
/// Authorization" — the andy-docs server will then reject with 401,
/// which the client treats as a normal upload failure (returns null →
/// metadata-only artifact). We deliberately do not block the request
/// on token-mint failures; the upstream best-effort contract still
/// applies.
/// </para>
/// </summary>
public sealed class ServiceBearerHandler : DelegatingHandler
{
    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
    private readonly ILogger<ServiceBearerHandler>? _logger;

    public ServiceBearerHandler(
        Func<CancellationToken, Task<string?>> tokenProvider,
        ILogger<ServiceBearerHandler>? logger = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? token = null;
        try
        {
            token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to obtain service bearer for outbound request to {Url}; sending anonymously.",
                request.RequestUri);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

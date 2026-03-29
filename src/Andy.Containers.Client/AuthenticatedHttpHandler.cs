using System.Net;
using System.Net.Http.Headers;

namespace Andy.Containers.Client;

/// <summary>
/// A delegating handler that attaches a Bearer token to every outgoing request.
/// </summary>
public sealed class AuthenticatedHttpHandler : DelegatingHandler
{
    private readonly Func<Task<string?>> _tokenProvider;

    public AuthenticatedHttpHandler(Func<Task<string?>> tokenProvider)
        : base(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            Console.Error.WriteLine($"Warning: 401 Unauthorized for {request.Method} {request.RequestUri}. Token may be expired.");

        return response;
    }
}

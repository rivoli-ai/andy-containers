using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Andy.Containers.Cli.Auth;

public class DeviceFlowAuthenticator
{
    private readonly string _authorityUrl;
    private readonly string _clientId;
    private readonly string _scope;

    public DeviceFlowAuthenticator(
        string authorityUrl,
        string clientId = "andy-containers-cli",
        string scope = "openid profile email")
    {
        _authorityUrl = authorityUrl.TrimEnd('/');
        _clientId = clientId;
        _scope = scope;
    }

    public async Task<StoredCredentials> AuthenticateAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();

        // Step 1: Request device authorization
        var deviceResponse = await RequestDeviceAuthorizationAsync(http, ct);

        // Step 2: Display code and prompt user
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]![/] First, copy your one-time code: [bold yellow]{deviceResponse.UserCode}[/]");
        AnsiConsole.MarkupLine(
            $"  Press [bold]Enter[/] to open [link]{deviceResponse.VerificationUri}[/] in your browser...");

        // Wait for Enter
        Console.ReadLine();

        // Step 3: Open browser
        OpenBrowser(deviceResponse.VerificationUri);

        // Step 4: Poll for token
        var interval = deviceResponse.Interval > 0 ? deviceResponse.Interval : 5;
        var expiresAt = DateTime.UtcNow.AddSeconds(deviceResponse.ExpiresIn);

        var credentials = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("yellow"))
            .StartAsync("Waiting for browser authentication...", async ctx =>
            {
                while (DateTime.UtcNow < expiresAt)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromSeconds(interval), ct);

                    var tokenResult = await PollTokenEndpointAsync(http, deviceResponse.DeviceCode, ct);

                    if (tokenResult.Error == "authorization_pending")
                        continue;

                    if (tokenResult.Error == "slow_down")
                    {
                        interval += 5;
                        continue;
                    }

                    if (tokenResult.Error == "expired_token")
                        throw new InvalidOperationException("Device code expired. Please try again.");

                    if (tokenResult.Error == "access_denied")
                        throw new InvalidOperationException("Authorization was denied.");

                    if (!string.IsNullOrEmpty(tokenResult.Error))
                        throw new InvalidOperationException($"Authentication error: {tokenResult.Error}");

                    // Success
                    return new StoredCredentials
                    {
                        AccessToken = tokenResult.AccessToken,
                        RefreshToken = tokenResult.RefreshToken,
                        ExpiresAt = tokenResult.ExpiresIn > 0
                            ? DateTime.UtcNow.AddSeconds(tokenResult.ExpiresIn)
                            : null,
                        AuthorityUrl = _authorityUrl,
                    };
                }

                throw new TimeoutException("Device code expired before authentication completed.");
            });

        return credentials;
    }

    /// <summary>
    /// Simple fallback for development: directly use a token.
    /// </summary>
    public static StoredCredentials AuthenticateWithToken(string token, string apiUrl)
    {
        return new StoredCredentials
        {
            AccessToken = token,
            ApiUrl = apiUrl,
        };
    }

    private async Task<DeviceAuthorizationResponse> RequestDeviceAuthorizationAsync(
        HttpClient http, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = _scope,
        });

        var response = await http.PostAsync(
            $"{_authorityUrl}/connect/device_authorization", content, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>(
            cancellationToken: ct);

        return result ?? throw new InvalidOperationException("Failed to parse device authorization response.");
    }

    private async Task<TokenResponse> PollTokenEndpointAsync(
        HttpClient http, string deviceCode, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
        });

        var response = await http.PostAsync(
            $"{_authorityUrl}/connect/token", content, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TokenResponse>(json) ?? new TokenResponse();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { UseShellExecute = false });
            }
        }
        catch
        {
            AnsiConsole.MarkupLine($"[dim]Could not open browser. Please navigate to:[/] {url}");
        }
    }

    private class DeviceAuthorizationResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = "";

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = "";

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = "";

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}

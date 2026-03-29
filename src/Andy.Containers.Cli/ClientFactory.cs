using Andy.Containers.Cli.Auth;
using Andy.Containers.Client;
using Spectre.Console;

namespace Andy.Containers.Cli;

/// <summary>
/// Creates an authenticated ContainersClient from stored credentials.
/// </summary>
public static class ClientFactory
{
    public static string DefaultApiUrl { get; set; } = "https://localhost:5200";

    public static ContainersClient Create()
    {
        var creds = CredentialStore.Load();
        if (creds?.AccessToken is null)
        {
            AnsiConsole.MarkupLine("[red]Not authenticated.[/] Run [bold]andy-containers auth login[/] first.");
            Environment.Exit(1);
        }

        if (CredentialStore.IsExpired(creds))
        {
            AnsiConsole.MarkupLine("[yellow]Token expired.[/] Run [bold]andy-containers auth login[/] to re-authenticate.");
            Environment.Exit(1);
        }

        var apiUrl = creds.ApiUrl ?? DefaultApiUrl;
        var handler = new AuthenticatedHttpHandler(() => Task.FromResult<string?>(creds.AccessToken));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
        return new ContainersClient(httpClient);
    }
}

using System.CommandLine;
using Andy.Containers.Cli.Auth;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

public static class AuthCommands
{
    public static Command Create()
    {
        var authCommand = new Command("auth", "Authentication management");

        authCommand.AddCommand(CreateLoginCommand());
        authCommand.AddCommand(CreateLogoutCommand());
        authCommand.AddCommand(CreateStatusCommand());

        return authCommand;
    }

    private static Command CreateLoginCommand()
    {
        var command = new Command("login", "Authenticate with Andy Containers");

        var tokenOption = new Option<string?>(
            "--token",
            "Directly provide an access token (for development)");

        var authorityOption = new Option<string?>(
            "--authority",
            "Override the OAuth authority URL");
        authorityOption.SetDefaultValue("https://localhost:5200");

        var apiUrlOption = new Option<string?>(
            "--api-url",
            "API server URL to store with credentials");

        command.AddOption(tokenOption);
        command.AddOption(authorityOption);
        command.AddOption(apiUrlOption);

        command.SetHandler(async (string? token, string? authority, string? apiUrl) =>
        {
            if (!string.IsNullOrEmpty(token))
            {
                // Direct token authentication (development fallback)
                var creds = DeviceFlowAuthenticator.AuthenticateWithToken(
                    token, apiUrl ?? "https://localhost:5200");
                CredentialStore.Save(creds);

                AnsiConsole.MarkupLine("[green]Logged in[/] with provided token.");
                AnsiConsole.MarkupLine($"  API URL: [dim]{creds.ApiUrl}[/]");
                AnsiConsole.MarkupLine($"  Credentials saved to [dim]{CredentialStore.GetCredentialPath()}[/]");
                return;
            }

            // Device flow authentication
            var authorityUrl = authority ?? "https://localhost:5200";
            var authenticator = new DeviceFlowAuthenticator(authorityUrl);

            try
            {
                var credentials = await authenticator.AuthenticateAsync();
                credentials.ApiUrl = apiUrl ?? "https://localhost:5200";

                CredentialStore.Save(credentials);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[green]Successfully authenticated![/]");
                if (!string.IsNullOrEmpty(credentials.UserEmail))
                    AnsiConsole.MarkupLine($"  Logged in as: [bold]{credentials.UserEmail}[/]");
                AnsiConsole.MarkupLine($"  Credentials saved to [dim]{CredentialStore.GetCredentialPath()}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {ex.Message}");
            }
        }, tokenOption, authorityOption, apiUrlOption);

        return command;
    }

    private static Command CreateLogoutCommand()
    {
        var command = new Command("logout", "Remove stored credentials");

        command.SetHandler(() =>
        {
            CredentialStore.Delete();
            AnsiConsole.MarkupLine("[green]Logged out.[/] Credentials removed.");
        });

        return command;
    }

    private static Command CreateStatusCommand()
    {
        var command = new Command("status", "Show current authentication status");

        command.SetHandler(() =>
        {
            var creds = CredentialStore.Load();
            if (creds == null)
            {
                AnsiConsole.MarkupLine("[yellow]Not authenticated.[/] Run [bold]andy-containers auth login[/] to sign in.");
                return;
            }

            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("Property");
            table.AddColumn("Value");

            table.AddRow("API URL", creds.ApiUrl ?? "[dim]not set[/]");
            table.AddRow("User", creds.UserEmail ?? "[dim]unknown[/]");
            table.AddRow("Authority", creds.AuthorityUrl ?? "[dim]not set[/]");

            if (creds.ExpiresAt.HasValue)
            {
                var expired = CredentialStore.IsExpired(creds);
                var expiryText = expired
                    ? $"[red]{creds.ExpiresAt.Value:u} (expired)[/]"
                    : $"[green]{creds.ExpiresAt.Value:u}[/]";
                table.AddRow("Expires", expiryText);
            }
            else
            {
                table.AddRow("Expires", "[dim]no expiry[/]");
            }

            var hasToken = !string.IsNullOrEmpty(creds.AccessToken);
            table.AddRow("Token", hasToken ? "[green]present[/]" : "[red]missing[/]");

            AnsiConsole.Write(table);
        });

        return command;
    }
}

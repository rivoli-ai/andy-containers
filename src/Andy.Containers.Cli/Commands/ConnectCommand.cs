using System.CommandLine;
using System.Diagnostics;
using Andy.Containers.Cli.Auth;
using Andy.Containers.Client;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

public static class ConnectCommand
{
    public static Command Create()
    {
        var cmd = new Command("connect", "Open an interactive terminal session to a container");
        var idArg = new Argument<string>("id", "Container ID");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (string id) =>
        {
            var client = ClientFactory.Create();

            // Step 1: Verify container is running
            var container = await client.GetContainerAsync(id);
            if (container.Status != "Running")
            {
                AnsiConsole.MarkupLine(
                    $"[red]Container is {Markup.Escape(container.Status)}, not Running.[/] " +
                    $"Start it first with [bold]andy-containers start {id}[/]");
                Environment.ExitCode = 1;
                return;
            }

            // Step 2: Get connection info
            var connInfo = await client.GetConnectionInfoAsync(id);

            // Step 3: Prefer SSH for native terminal experience
            if (!string.IsNullOrEmpty(connInfo.SshEndpoint))
            {
                AnsiConsole.MarkupLine(
                    $"[green]Connecting to[/] [bold]{Markup.Escape(container.Name)}[/] [green]via SSH...[/]");
                AnsiConsole.WriteLine();

                var parts = connInfo.SshEndpoint.Split(' ');
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = parts[0],
                        Arguments = string.Join(' ', parts[1..]),
                        UseShellExecute = false,
                    }
                };

                process.Start();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    AnsiConsole.MarkupLine($"[dim]SSH session exited with code {process.ExitCode}[/]");

                Environment.ExitCode = process.ExitCode;
                return;
            }

            // Step 4: No SSH available - suggest alternatives
            AnsiConsole.MarkupLine("[yellow]SSH not available for this container.[/]");
            AnsiConsole.WriteLine();

            if (!string.IsNullOrEmpty(container.ExternalId))
            {
                var shortExternalId = container.ExternalId.Length > 12
                    ? container.ExternalId[..12]
                    : container.ExternalId;
                AnsiConsole.MarkupLine("  [bold]Docker exec:[/]");
                AnsiConsole.MarkupLine($"    [cyan]docker exec -it {Markup.Escape(shortExternalId)} /bin/bash[/]");
                AnsiConsole.WriteLine();
            }

            if (!string.IsNullOrEmpty(connInfo.IdeEndpoint))
            {
                AnsiConsole.MarkupLine("  [bold]Web terminal:[/]");
                AnsiConsole.MarkupLine($"    [link]{Markup.Escape(connInfo.IdeEndpoint)}[/]");
            }
            else
            {
                var creds = CredentialStore.Load();
                var apiUrl = (creds?.ApiUrl ?? "https://localhost:5200").TrimEnd('/');
                AnsiConsole.MarkupLine("  [bold]Web terminal:[/]");
                AnsiConsole.MarkupLine($"    [link]{apiUrl}/containers/{id}/terminal[/]");
            }
        }, idArg);
        return cmd;
    }
}

using System.CommandLine;
using Andy.Containers.Cli.Auth;
using Andy.Containers.Cli.Formatting;
using Andy.Containers.Client;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

public static class ContainerCommands
{
    public static Command CreateListCommand()
    {
        var cmd = new Command("list", "List containers");
        cmd.SetHandler(async () =>
        {
            var client = ClientFactory.Create();
            var result = await client.ListContainersAsync(100);
            TableFormatter.PrintContainerTable(result.Items);
        });
        return cmd;
    }

    public static Command CreateInfoCommand()
    {
        var cmd = new Command("info", "Show container details");
        var idArg = new Argument<string>("id", "Container ID or prefix");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (string id) =>
        {
            var client = ClientFactory.Create();
            var c = await client.GetContainerAsync(id);
            TableFormatter.PrintContainerDetail(c);
        }, idArg);
        return cmd;
    }

    public static Command CreateStartCommand()
    {
        var cmd = new Command("start", "Start a stopped container");
        var idArg = new Argument<string>("id", "Container ID");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (string id) =>
        {
            var client = ClientFactory.Create();
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Starting container...", async _ =>
                {
                    await client.StartContainerAsync(id);
                });
            AnsiConsole.MarkupLine($"[green]Container started.[/] ({id[..Math.Min(8, id.Length)]})");
        }, idArg);
        return cmd;
    }

    public static Command CreateStopCommand()
    {
        var cmd = new Command("stop", "Stop a running container");
        var idArg = new Argument<string>("id", "Container ID");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (string id) =>
        {
            var client = ClientFactory.Create();
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Stopping container...", async _ =>
                {
                    await client.StopContainerAsync(id);
                });
            AnsiConsole.MarkupLine($"[green]Container stopped.[/] ({id[..Math.Min(8, id.Length)]})");
        }, idArg);
        return cmd;
    }

    public static Command CreateDestroyCommand()
    {
        var cmd = new Command("destroy", "Permanently destroy a container");
        var idArg = new Argument<string>("id", "Container ID");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (string id) =>
        {
            var client = ClientFactory.Create();
            var container = await client.GetContainerAsync(id);

            if (!AnsiConsole.Confirm(
                    $"[red]Destroy[/] container [bold]{Markup.Escape(container.Name)}[/] ({id[..Math.Min(8, id.Length)]})?",
                    defaultValue: false))
            {
                AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                return;
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Destroying container...", async _ =>
                {
                    await client.DestroyContainerAsync(id);
                });
            AnsiConsole.MarkupLine($"[green]Container destroyed.[/] ({id[..Math.Min(8, id.Length)]})");
        }, idArg);
        return cmd;
    }

    public static Command CreateCreateCommand()
    {
        var cmd = new Command("create", "Create a new container");
        var nameOpt = new Option<string>("--name", "Container name") { IsRequired = true };
        var templateOpt = new Option<string>("--template", "Template code") { IsRequired = true };
        var providerOpt = new Option<string?>("--provider", "Provider code");
        cmd.AddOption(nameOpt);
        cmd.AddOption(templateOpt);
        cmd.AddOption(providerOpt);
        cmd.SetHandler(async (string name, string template, string? provider) =>
        {
            var client = ClientFactory.Create();
            ContainersClient.ContainerDto container = null!;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Creating container [bold]{Markup.Escape(name)}[/]...", async _ =>
                {
                    container = await client.CreateContainerAsync(name, template, provider);
                });
            AnsiConsole.MarkupLine($"[green]Container created.[/]");
            AnsiConsole.MarkupLine($"  ID:     [bold]{container.Id}[/]");
            AnsiConsole.MarkupLine($"  Name:   [bold]{Markup.Escape(container.Name)}[/]");
            AnsiConsole.MarkupLine($"  Status: {container.Status}");
        }, nameOpt, templateOpt, providerOpt);
        return cmd;
    }

    public static Command CreateExecCommand()
    {
        var cmd = new Command("exec", "Execute a command in a container");
        var idArg = new Argument<string>("id", "Container ID");
        var cmdArg = new Argument<string>("command", "Command to execute");
        cmd.AddArgument(idArg);
        cmd.AddArgument(cmdArg);
        cmd.SetHandler(async (string id, string command) =>
        {
            var client = ClientFactory.Create();
            var result = await client.ExecAsync(id, command);

            if (!string.IsNullOrEmpty(result.StdOut))
                Console.Write(result.StdOut);

            if (!string.IsNullOrEmpty(result.StdErr))
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.StdErr)}[/]");

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[dim]Exit code: {result.ExitCode}[/]");
                Environment.ExitCode = result.ExitCode;
            }
        }, idArg, cmdArg);
        return cmd;
    }

    public static Command CreateStatsCommand()
    {
        var cmd = new Command("stats", "Show container resource usage");
        var idArg = new Argument<string>("id", "Container ID");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (string id) =>
        {
            var client = ClientFactory.Create();
            var stats = await client.GetStatsAsync(id);
            TableFormatter.PrintStats(stats);
        }, idArg);
        return cmd;
    }
}

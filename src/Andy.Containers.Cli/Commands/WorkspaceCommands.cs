using System.CommandLine;
using System.Text.Json;
using Andy.Containers.Cli.Formatting;
using Andy.Containers.Client;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

/// <summary>
/// rivoli-ai/andy-containers#189. Wraps the <c>/api/workspaces</c>
/// surface (list / get / create / delete). Update is intentionally
/// omitted — operators rarely need it from CLI and the
/// <c>UpdateWorkspaceDto</c> doesn't expose the governance fields
/// anyway (X5 keeps the EnvironmentProfile binding immutable).
/// </summary>
public static class WorkspaceCommands
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Command Create()
    {
        var cmd = new Command("workspace", "Manage workspaces");
        cmd.AddCommand(CreateListCommand());
        cmd.AddCommand(CreateGetCommand());
        cmd.AddCommand(CreateCreateCommand());
        cmd.AddCommand(CreateDeleteCommand());
        return cmd;
    }

    private static Command CreateListCommand()
    {
        var cmd = new Command("list", "List workspaces");
        var ownerOpt = new Option<string?>("--owner", "Filter by owner id (admin only).");
        var orgOpt = new Option<string?>("--organization", "Filter by organization id (GUID).");
        var formatOpt = OutputFormatOption.Create();
        cmd.AddOption(ownerOpt);
        cmd.AddOption(orgOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var owner = context.ParseResult.GetValueForOption(ownerOpt);
            var orgRaw = context.ParseResult.GetValueForOption(orgOpt);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            Guid? orgId = null;
            if (!string.IsNullOrWhiteSpace(orgRaw))
            {
                if (!Guid.TryParse(orgRaw, out var parsed) || parsed == Guid.Empty)
                {
                    AnsiConsole.MarkupLine("[red]--organization must be a non-empty GUID.[/]");
                    context.ExitCode = 2;
                    return;
                }
                orgId = parsed;
            }

            var client = ClientFactory.Create();
            var page = await client.ListWorkspacesAsync(owner, orgId, ct: ct);

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(page.Items, JsonOut));
                return;
            }

            TableFormatter.PrintWorkspaceTable(page.Items);
            if (page.TotalCount > page.Items.Length)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]Showing {page.Items.Length} of {page.TotalCount} workspace(s).[/]");
            }
        });

        return cmd;
    }

    private static Command CreateGetCommand()
    {
        var cmd = new Command("get", "Show workspace details");
        var idArg = new Argument<string>("id", "Workspace id (GUID).");
        var formatOpt = OutputFormatOption.Create();
        cmd.AddArgument(idArg);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();
            ContainersClient.WorkspaceDto ws;
            try
            {
                ws = await client.GetWorkspaceAsync(id, ct);
            }
            catch (ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Workspace '{Markup.Escape(id)}' not found.[/]");
                context.ExitCode = 4;
                return;
            }

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(ws, JsonOut));
                return;
            }

            TableFormatter.PrintWorkspaceDetail(ws);
        });

        return cmd;
    }

    private static Command CreateCreateCommand()
    {
        var cmd = new Command("create", "Create a workspace");
        var nameArg = new Argument<string>("name", "Workspace name.");
        var profileOpt = new Option<string>(
            "--environment-profile",
            "EnvironmentProfile slug (e.g. 'headless-container'). Required (X5 governance anchor).")
        {
            IsRequired = true,
        };
        var descOpt = new Option<string?>("--description", "Optional description.");
        var orgOpt = new Option<string?>("--organization", "Optional organization id (GUID).");
        var teamOpt = new Option<string?>("--team", "Optional team id (GUID).");
        var gitUrlOpt = new Option<string?>("--git-repo", "Optional primary git repository URL.");
        var gitBranchOpt = new Option<string?>("--branch", "Optional git branch.");
        var formatOpt = OutputFormatOption.Create();

        cmd.AddArgument(nameArg);
        cmd.AddOption(profileOpt);
        cmd.AddOption(descOpt);
        cmd.AddOption(orgOpt);
        cmd.AddOption(teamOpt);
        cmd.AddOption(gitUrlOpt);
        cmd.AddOption(gitBranchOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var name = context.ParseResult.GetValueForArgument(nameArg);
            var profile = context.ParseResult.GetValueForOption(profileOpt)!;
            var description = context.ParseResult.GetValueForOption(descOpt);
            var orgRaw = context.ParseResult.GetValueForOption(orgOpt);
            var teamRaw = context.ParseResult.GetValueForOption(teamOpt);
            var gitUrl = context.ParseResult.GetValueForOption(gitUrlOpt);
            var gitBranch = context.ParseResult.GetValueForOption(gitBranchOpt);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            Guid? ParseOptionalGuid(string? raw, string flagName)
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                if (!Guid.TryParse(raw, out var g) || g == Guid.Empty)
                {
                    AnsiConsole.MarkupLine($"[red]{flagName} must be a non-empty GUID.[/]");
                    context.ExitCode = 2;
                    return null;
                }
                return g;
            }

            var orgId = ParseOptionalGuid(orgRaw, "--organization");
            if (context.ExitCode != 0) return;
            var teamId = ParseOptionalGuid(teamRaw, "--team");
            if (context.ExitCode != 0) return;

            var request = new ContainersClient.CreateWorkspaceRequest(
                name, description, orgId, teamId, gitUrl, gitBranch, profile);

            var client = ClientFactory.Create();
            ContainersClient.WorkspaceDto ws;
            try
            {
                ws = await client.CreateWorkspaceAsync(request, ct);
            }
            catch (ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Bad request:[/] {Markup.Escape(ex.ResponseBody ?? ex.Message)}");
                context.ExitCode = 2;
                return;
            }

            AnsiConsole.MarkupLine(
                $"[green]Created workspace[/] [bold]{Markup.Escape(ws.Name)}[/] ([dim]{ws.Id}[/])");

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(ws, JsonOut));
                return;
            }

            TableFormatter.PrintWorkspaceDetail(ws);
        });

        return cmd;
    }

    private static Command CreateDeleteCommand()
    {
        var cmd = new Command("delete", "Delete a workspace");
        var idArg = new Argument<string>("id", "Workspace id (GUID).");
        cmd.AddArgument(idArg);

        cmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();
            try
            {
                await client.DeleteWorkspaceAsync(id, ct);
            }
            catch (ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Workspace '{Markup.Escape(id)}' not found.[/]");
                context.ExitCode = 4;
                return;
            }

            AnsiConsole.MarkupLine($"[green]Deleted workspace[/] [dim]{Markup.Escape(id)}[/]");
        });

        return cmd;
    }
}

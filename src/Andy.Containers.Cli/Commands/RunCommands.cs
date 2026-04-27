using System.CommandLine;
using Andy.Containers.Client;
using Andy.Containers.Models;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

/// <summary>
/// AP9 (rivoli-ai/andy-containers#111). <c>andy-containers-cli runs ...</c>
/// — four subcommands wrapping the <c>/api/runs</c> surface from AP1-AP7
/// plus the AP9 NDJSON events endpoint. Pattern mirrors
/// <see cref="ContainerCommands"/>: one static factory per subcommand,
/// composed into a parent <c>runs</c> command.
/// </summary>
public static class RunCommands
{
    public static Command Create()
    {
        var cmd = new Command("runs", "Manage agent runs (Epic AP)");
        cmd.AddCommand(CreateCreateCommand());
        cmd.AddCommand(CreateGetCommand());
        cmd.AddCommand(CreateCancelCommand());
        cmd.AddCommand(CreateEventsCommand());
        return cmd;
    }

    private static Command CreateCreateCommand()
    {
        var cmd = new Command("create", "Submit a new agent run");

        var agentArg = new Argument<string>("agent", "Agent slug from andy-agents (e.g. 'triage-agent').");
        var modeOpt = new Option<string>("--mode", () => "Headless", "Run mode: Headless, Terminal, Desktop.");
        var profileOpt = new Option<string>("--environment-profile", "EnvironmentProfile id (GUID).") { IsRequired = true };
        var revisionOpt = new Option<int?>("--agent-revision", "Optional agent revision pin; omit for head.");
        var workspaceOpt = new Option<string?>("--workspace", "Optional workspace id (GUID).");
        var branchOpt = new Option<string?>("--branch", "Optional branch within the workspace.");
        var policyOpt = new Option<string?>("--policy", "Optional policy id (GUID).");
        var correlationOpt = new Option<string?>("--correlation-id", "Optional ADR-0001 correlation root (GUID); minted if omitted.");

        cmd.AddArgument(agentArg);
        cmd.AddOption(modeOpt);
        cmd.AddOption(profileOpt);
        cmd.AddOption(revisionOpt);
        cmd.AddOption(workspaceOpt);
        cmd.AddOption(branchOpt);
        cmd.AddOption(policyOpt);
        cmd.AddOption(correlationOpt);

        cmd.SetHandler(async (context) =>
        {
            var agent = context.ParseResult.GetValueForArgument(agentArg);
            var modeStr = context.ParseResult.GetValueForOption(modeOpt) ?? "Headless";
            var profileStr = context.ParseResult.GetValueForOption(profileOpt)!;

            if (!Enum.TryParse<RunMode>(modeStr, ignoreCase: true, out var mode))
            {
                AnsiConsole.MarkupLine($"[red]Invalid mode '{Markup.Escape(modeStr)}'.[/] Use Headless, Terminal, or Desktop.");
                context.ExitCode = 2;
                return;
            }
            if (!Guid.TryParse(profileStr, out var profileId) || profileId == Guid.Empty)
            {
                AnsiConsole.MarkupLine($"[red]--environment-profile must be a non-empty GUID.[/]");
                context.ExitCode = 2;
                return;
            }

            var request = new CreateRunRequest
            {
                AgentId = agent,
                AgentRevision = context.ParseResult.GetValueForOption(revisionOpt),
                Mode = mode,
                EnvironmentProfileId = profileId,
                PolicyId = ParseOptionalGuid(context.ParseResult.GetValueForOption(policyOpt)),
                CorrelationId = ParseOptionalGuid(context.ParseResult.GetValueForOption(correlationOpt)),
            };

            var workspaceStr = context.ParseResult.GetValueForOption(workspaceOpt);
            if (!string.IsNullOrWhiteSpace(workspaceStr))
            {
                if (!Guid.TryParse(workspaceStr, out var wid) || wid == Guid.Empty)
                {
                    AnsiConsole.MarkupLine($"[red]--workspace must be a non-empty GUID.[/]");
                    context.ExitCode = 2;
                    return;
                }
                request.WorkspaceRef = new WorkspaceRefRequest
                {
                    WorkspaceId = wid,
                    Branch = context.ParseResult.GetValueForOption(branchOpt),
                };
            }

            var client = ClientFactory.Create();
            var run = await client.CreateRunAsync(request, context.GetCancellationToken());
            PrintRunDetail(run);
        });

        return cmd;
    }

    private static Command CreateGetCommand()
    {
        var cmd = new Command("get", "Show run details");
        var idArg = new Argument<string>("id", "Run id (GUID).");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var ct = context.GetCancellationToken();
            var client = ClientFactory.Create();
            var run = await client.GetRunAsync(id, ct);
            PrintRunDetail(run);
        });
        return cmd;
    }

    private static Command CreateCancelCommand()
    {
        var cmd = new Command("cancel", "Cancel a non-terminal run");
        var idArg = new Argument<string>("id", "Run id (GUID).");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var ct = context.GetCancellationToken();
            var client = ClientFactory.Create();
            RunDto? run = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Cancelling run...", async _ => { run = await client.CancelRunAsync(id, ct); });
            AnsiConsole.MarkupLine($"[green]Run cancelled.[/] Status: [yellow]{run!.Status}[/]");
            PrintRunDetail(run);
        });
        return cmd;
    }

    private static Command CreateEventsCommand()
    {
        var cmd = new Command("events", "Stream lifecycle events for a run until it terminates");
        var idArg = new Argument<string>("id", "Run id (GUID).");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var ct = context.GetCancellationToken();
            var client = ClientFactory.Create();
            AnsiConsole.MarkupLine($"[dim]Streaming events for run {id[..Math.Min(8, id.Length)]} (Ctrl+C to stop)...[/]");

            await foreach (var evt in client.StreamRunEventsAsync(id, ct))
            {
                var color = evt.Kind switch
                {
                    "finished" => "green",
                    "failed" => "red",
                    "cancelled" => "yellow",
                    "timeout" => "magenta",
                    _ => "white"
                };
                var ts = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
                var exit = evt.ExitCode is { } ec ? $" exit={ec}" : "";
                var dur = evt.DurationSeconds is { } d ? $" dur={d:F1}s" : "";
                AnsiConsole.MarkupLine(
                    $"[dim]{ts}[/] [{color}]{evt.Kind}[/] status={Markup.Escape(evt.Status)}{exit}{dur}");
            }

            AnsiConsole.MarkupLine("[dim]Stream closed (run reached terminal state).[/]");
        });
        return cmd;
    }

    private static Guid? ParseOptionalGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var g) ? g : null;
    }

    private static void PrintRunDetail(RunDto run)
    {
        var statusColor = run.Status switch
        {
            RunStatus.Running => "green",
            RunStatus.Pending or RunStatus.Provisioning => "cyan",
            RunStatus.Succeeded => "green",
            RunStatus.Cancelled => "yellow",
            RunStatus.Failed or RunStatus.Timeout => "red",
            _ => "dim"
        };

        var table = new Table()
            .Border(TableBorder.Rounded)
            .HideHeaders()
            .AddColumn("")
            .AddColumn("");

        table.AddRow("Id", run.Id.ToString());
        table.AddRow("Agent", Markup.Escape(run.AgentId));
        if (run.AgentRevision is not null) table.AddRow("Revision", run.AgentRevision.ToString()!);
        table.AddRow("Mode", run.Mode.ToString());
        table.AddRow("Status", $"[{statusColor}]{run.Status}[/]");
        if (run.ContainerId is not null) table.AddRow("Container", run.ContainerId.ToString()!);
        table.AddRow("Created", run.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (run.StartedAt is not null) table.AddRow("Started", run.StartedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (run.EndedAt is not null) table.AddRow("Ended", run.EndedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (run.ExitCode is not null) table.AddRow("Exit", run.ExitCode.ToString()!);
        if (!string.IsNullOrEmpty(run.Error)) table.AddRow("Error", $"[red]{Markup.Escape(run.Error)}[/]");
        table.AddRow("Correlation", run.CorrelationId.ToString());

        AnsiConsole.Write(table);
    }
}

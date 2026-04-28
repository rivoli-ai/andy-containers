using System.CommandLine;
using System.Text.Json;
using Andy.Containers.Cli.Formatting;
using Andy.Containers.Models;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

/// <summary>
/// X7 (rivoli-ai/andy-containers#97). <c>andy-containers-cli environments
/// {list,get}</c> — read-only browse over the X3 catalog. Mirrors the
/// vocabulary established by <c>andy-conductor-cli</c> (Epic AN); shared
/// <c>--format</c> contract via <see cref="OutputFormatOption"/>.
/// </summary>
public static class EnvironmentCommands
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Command Create()
    {
        var cmd = new Command("environments", "Browse the EnvironmentProfile catalog (Epic X)");
        cmd.AddCommand(CreateListCommand());
        cmd.AddCommand(CreateGetCommand());
        return cmd;
    }

    private static Command CreateListCommand()
    {
        var cmd = new Command("list", "List environment profiles");

        var kindOpt = new Option<string?>(
            "--kind",
            "Filter by kind: HeadlessContainer, Terminal, or Desktop (case-insensitive).");
        var formatOpt = OutputFormatOption.Create();

        cmd.AddOption(kindOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var kind = context.ParseResult.GetValueForOption(kindOpt);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();
            var page = await client.ListEnvironmentsAsync(kind, ct: ct);

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(page.Items, JsonOut));
                return;
            }

            TableFormatter.PrintEnvironmentTable(page.Items);
            if (page.TotalCount > page.Items.Length)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]Showing {page.Items.Length} of {page.TotalCount} profile(s).[/]");
            }
        });

        return cmd;
    }

    private static Command CreateGetCommand()
    {
        var cmd = new Command("get", "Show one environment profile by code");

        var codeArg = new Argument<string>("code",
            "Profile code (slug, e.g. 'headless-container').");
        var formatOpt = OutputFormatOption.Create();

        cmd.AddArgument(codeArg);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var code = context.ParseResult.GetValueForArgument(codeArg);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();
            EnvironmentProfileDto profile;
            try
            {
                profile = await client.GetEnvironmentByCodeAsync(code, ct);
            }
            catch (Andy.Containers.Client.ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Profile '{Markup.Escape(code)}' not found.[/]");
                // Epic AN exit-code contract: 4 = not-found.
                context.ExitCode = 4;
                return;
            }

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(profile, JsonOut));
                return;
            }

            TableFormatter.PrintEnvironmentDetail(profile);
        });

        return cmd;
    }
}

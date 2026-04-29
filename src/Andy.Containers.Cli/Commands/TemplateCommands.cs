using System.CommandLine;
using System.Text.Json;
using Andy.Containers.Cli.Formatting;
using Andy.Containers.Client;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

/// <summary>
/// rivoli-ai/andy-containers#190. Read-only browse over the
/// <c>/api/templates</c> catalog. CRUD + publish + image-build
/// remain admin-only via the REST/UI surface.
/// </summary>
public static class TemplateCommands
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Command Create()
    {
        var cmd = new Command("templates", "Browse the container template catalog");
        cmd.AddCommand(CreateListCommand());
        cmd.AddCommand(CreateInfoCommand());
        cmd.AddCommand(CreateDefinitionCommand());
        return cmd;
    }

    private static Command CreateListCommand()
    {
        var cmd = new Command("list", "List templates");
        var scopeOpt = new Option<string?>(
            "--scope", "Filter by scope: Global, Organization, Team, User.");
        var searchOpt = new Option<string?>(
            "--search", "Filter by name / description / code substring.");
        var formatOpt = OutputFormatOption.Create();
        cmd.AddOption(scopeOpt);
        cmd.AddOption(searchOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var scope = context.ParseResult.GetValueForOption(scopeOpt);
            var search = context.ParseResult.GetValueForOption(searchOpt);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();
            var page = await client.ListTemplatesAsync(scope, search, ct: ct);

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(page.Items, JsonOut));
                return;
            }

            TableFormatter.PrintTemplateTable(page.Items);
            if (page.TotalCount > page.Items.Length)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]Showing {page.Items.Length} of {page.TotalCount} template(s).[/]");
            }
        });

        return cmd;
    }

    private static Command CreateInfoCommand()
    {
        var cmd = new Command("info", "Show template details");
        var codeArg = new Argument<string>("code", "Template code (slug, e.g. 'andy-cli-dev').");
        var formatOpt = OutputFormatOption.Create();
        cmd.AddArgument(codeArg);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var code = context.ParseResult.GetValueForArgument(codeArg);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();
            ContainersClient.TemplateDetailDto t;
            try
            {
                t = await client.GetTemplateByCodeAsync(code, ct);
            }
            catch (ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Template '{Markup.Escape(code)}' not found.[/]");
                context.ExitCode = 4;
                return;
            }

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(t, JsonOut));
                return;
            }

            TableFormatter.PrintTemplateDetail(t);
        });

        return cmd;
    }

    private static Command CreateDefinitionCommand()
    {
        var cmd = new Command("definition",
            "Print the template's YAML definition. Pipe into editors / yq / etc.");
        var codeArg = new Argument<string>("code",
            "Template code or id. Resolves to the row's id internally.");
        cmd.AddArgument(codeArg);

        cmd.SetHandler(async (context) =>
        {
            var codeOrId = context.ParseResult.GetValueForArgument(codeArg);
            var ct = context.GetCancellationToken();

            var client = ClientFactory.Create();

            // The /definition endpoint takes an id; resolve via by-code
            // first when the caller passed a slug. Distinguish using a
            // GUID-parse — robust enough for the two input shapes.
            string id;
            if (Guid.TryParse(codeOrId, out _))
            {
                id = codeOrId;
            }
            else
            {
                try
                {
                    var t = await client.GetTemplateByCodeAsync(codeOrId, ct);
                    id = t.Id.ToString();
                }
                catch (ContainersApiException ex)
                    when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    AnsiConsole.MarkupLine($"[red]Template '{Markup.Escape(codeOrId)}' not found.[/]");
                    context.ExitCode = 4;
                    return;
                }
            }

            ContainersClient.TemplateDefinitionDto def;
            try
            {
                def = await client.GetTemplateDefinitionAsync(id, ct);
            }
            catch (ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Template '{Markup.Escape(codeOrId)}' not found.[/]");
                context.ExitCode = 4;
                return;
            }

            // Plain Console.Write — no Spectre markup — so `... > file.yaml`
            // captures clean YAML without ANSI escape sequences.
            Console.Write(def.Content);
            if (!def.Content.EndsWith('\n'))
            {
                Console.WriteLine();
            }
        });

        return cmd;
    }
}

using System.CommandLine;
using System.Text.Json;
using Andy.Containers.Cli.Formatting;
using Andy.Containers.Client;
using Spectre.Console;

namespace Andy.Containers.Cli.Commands;

/// <summary>
/// rivoli-ai/andy-containers#191. Read-only ops surface for
/// multi-provider deployments. <c>list</c> shows every infrastructure
/// provider; <c>health</c> probes one. CRUD remains admin-only via
/// the REST/UI surface.
/// </summary>
public static class ProviderCommands
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Command Create()
    {
        var cmd = new Command("providers", "List + probe infrastructure providers");
        cmd.AddCommand(CreateListCommand());
        cmd.AddCommand(CreateHealthCommand());
        return cmd;
    }

    private static Command CreateListCommand()
    {
        var cmd = new Command("list", "List infrastructure providers");
        var orgOpt = new Option<string?>(
            "--organization", "Filter by organization id (GUID).");
        var formatOpt = OutputFormatOption.Create();
        cmd.AddOption(orgOpt);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
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
            var providers = await client.ListProvidersAsync(orgId, ct);

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(providers, JsonOut));
                return;
            }

            TableFormatter.PrintProviderTable(providers);
        });

        return cmd;
    }

    private static Command CreateHealthCommand()
    {
        var cmd = new Command("health",
            "Probe a provider's health and capabilities. Triggers a live check against the underlying infrastructure.");
        var idArg = new Argument<string>("id",
            "Provider id (GUID). Resolves via 'providers list' if you only have a code.");
        var formatOpt = OutputFormatOption.Create();
        cmd.AddArgument(idArg);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (context) =>
        {
            var id = context.ParseResult.GetValueForArgument(idArg);
            var format = context.ParseResult.GetValueForOption(formatOpt);
            var ct = context.GetCancellationToken();

            if (!Guid.TryParse(id, out _))
            {
                AnsiConsole.MarkupLine($"[red]Provider id must be a GUID. Use 'providers list' to find it.[/]");
                context.ExitCode = 2;
                return;
            }

            var client = ClientFactory.Create();
            ContainersClient.ProviderHealthDto health;
            try
            {
                health = await client.GetProviderHealthAsync(id, ct);
            }
            catch (ContainersApiException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine($"[red]Provider '{Markup.Escape(id)}' not found.[/]");
                context.ExitCode = 4;
                return;
            }

            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(health, JsonOut));
                return;
            }

            TableFormatter.PrintProviderHealth(id, health);
        });

        return cmd;
    }
}

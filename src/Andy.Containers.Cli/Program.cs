using System.CommandLine;
using Andy.Containers.Cli;
using Andy.Containers.Cli.Commands;

var rootCommand = new RootCommand("Andy Containers CLI - Manage development containers");

// Global options
var apiUrlOption = new Option<string>("--api-url", "API server URL");
apiUrlOption.SetDefaultValue("https://localhost:5200");
rootCommand.AddGlobalOption(apiUrlOption);

// Wire up the global API URL
rootCommand.AddValidator(result =>
{
    var apiUrl = result.GetValueForOption(apiUrlOption);
    if (!string.IsNullOrEmpty(apiUrl))
        ClientFactory.DefaultApiUrl = apiUrl;
});

// Auth commands
rootCommand.AddCommand(AuthCommands.Create());

// Container commands (implemented)
rootCommand.AddCommand(ContainerCommands.CreateListCommand());
rootCommand.AddCommand(ContainerCommands.CreateInfoCommand());
rootCommand.AddCommand(ContainerCommands.CreateStartCommand());
rootCommand.AddCommand(ContainerCommands.CreateStopCommand());
rootCommand.AddCommand(ContainerCommands.CreateDestroyCommand());
rootCommand.AddCommand(ContainerCommands.CreateCreateCommand());
rootCommand.AddCommand(ContainerCommands.CreateExecCommand());
rootCommand.AddCommand(ContainerCommands.CreateStatsCommand());
rootCommand.AddCommand(ConnectCommand.Create());

// Run commands (AP9 — rivoli-ai/andy-containers#111).
rootCommand.AddCommand(RunCommands.Create());

// Workspace commands (stubs for future)
var workspaceCommand = new Command("workspace", "Manage workspaces");
workspaceCommand.AddCommand(new Command("create", "Create a workspace"));
workspaceCommand.AddCommand(new Command("list", "List workspaces"));
rootCommand.AddCommand(workspaceCommand);

// Template commands (stubs for future)
var templatesCommand = new Command("templates", "Manage template catalog");
templatesCommand.AddCommand(new Command("list", "Browse template catalog"));
rootCommand.AddCommand(templatesCommand);

return await rootCommand.InvokeAsync(args);

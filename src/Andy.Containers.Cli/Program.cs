using System.CommandLine;

var rootCommand = new RootCommand("Andy Containers CLI - Manage development containers");

// Global options
var apiUrlOption = new Option<string>("--api-url", "API server URL");
apiUrlOption.SetDefaultValue("https://localhost:5200");
rootCommand.AddGlobalOption(apiUrlOption);

var outputOption = new Option<string>("--output", "Output format: table, json, csv");
outputOption.SetDefaultValue("table");
rootCommand.AddGlobalOption(outputOption);

// Container commands
var createCommand = new Command("create", "Create a new container");
createCommand.AddOption(new Option<string>("--template", "Template code") { IsRequired = true });
createCommand.AddOption(new Option<string>("--name", "Container name") { IsRequired = true });
createCommand.AddOption(new Option<string>("--provider", "Provider code"));
createCommand.AddOption(new Option<string>("--git", "Git repository URL"));
createCommand.AddOption(new Option<string>("--branch", "Git branch"));
createCommand.SetHandler(() => Console.WriteLine("TODO: Implement create container"));
rootCommand.AddCommand(createCommand);

var listCommand = new Command("list", "List containers");
listCommand.SetHandler(() => Console.WriteLine("TODO: Implement list containers"));
rootCommand.AddCommand(listCommand);

var startCommand = new Command("start", "Start a container");
startCommand.AddArgument(new Argument<string>("id", "Container ID"));
startCommand.SetHandler((string id) => Console.WriteLine($"TODO: Start container {id}"),
    startCommand.Arguments[0] as Argument<string> ?? throw new InvalidOperationException());
rootCommand.AddCommand(startCommand);

var stopCommand = new Command("stop", "Stop a container");
stopCommand.AddArgument(new Argument<string>("id", "Container ID"));
stopCommand.SetHandler((string id) => Console.WriteLine($"TODO: Stop container {id}"),
    stopCommand.Arguments[0] as Argument<string> ?? throw new InvalidOperationException());
rootCommand.AddCommand(stopCommand);

var destroyCommand = new Command("destroy", "Destroy a container");
destroyCommand.AddArgument(new Argument<string>("id", "Container ID"));
destroyCommand.SetHandler((string id) => Console.WriteLine($"TODO: Destroy container {id}"),
    destroyCommand.Arguments[0] as Argument<string> ?? throw new InvalidOperationException());
rootCommand.AddCommand(destroyCommand);

var execCommand = new Command("exec", "Execute a command in a container");
execCommand.AddArgument(new Argument<string>("id", "Container ID"));
execCommand.AddArgument(new Argument<string>("command", "Command to execute"));
execCommand.SetHandler((string id, string command) => Console.WriteLine($"TODO: Exec '{command}' in {id}"),
    execCommand.Arguments[0] as Argument<string> ?? throw new InvalidOperationException(),
    execCommand.Arguments[1] as Argument<string> ?? throw new InvalidOperationException());
rootCommand.AddCommand(execCommand);

// Workspace commands
var workspaceCommand = new Command("workspace", "Manage workspaces");
workspaceCommand.AddCommand(new Command("create", "Create a workspace"));
workspaceCommand.AddCommand(new Command("list", "List workspaces"));
rootCommand.AddCommand(workspaceCommand);

// Template commands
var templatesCommand = new Command("templates", "Manage template catalog");
templatesCommand.AddCommand(new Command("list", "Browse template catalog"));
templatesCommand.AddCommand(new Command("create", "Create a template from YAML"));
templatesCommand.AddCommand(new Command("build", "Build image for a template"));
rootCommand.AddCommand(templatesCommand);

// Image commands
var imagesCommand = new Command("images", "Manage container images");
imagesCommand.AddCommand(new Command("list", "List built images"));
imagesCommand.AddCommand(new Command("build", "Trigger image build"));
imagesCommand.AddCommand(new Command("diff", "Compare two images"));
rootCommand.AddCommand(imagesCommand);

// Config commands
var configCommand = new Command("config", "YAML configuration management");
configCommand.AddCommand(new Command("sync", "Sync database from YAML files"));
configCommand.AddCommand(new Command("validate", "Validate YAML configuration"));
configCommand.AddCommand(new Command("diff", "Show diff between YAML and database"));
configCommand.AddCommand(new Command("export", "Export database config to YAML"));
rootCommand.AddCommand(configCommand);

return await rootCommand.InvokeAsync(args);

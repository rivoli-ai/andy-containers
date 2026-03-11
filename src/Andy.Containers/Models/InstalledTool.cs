namespace Andy.Containers.Models;

public class InstalledTool
{
    public required string Name { get; set; }
    public required string Version { get; set; }
    public DependencyType Type { get; set; }
    public string? DeclaredVersion { get; set; }
    public bool MatchesDeclared { get; set; }
    public string? InstallPath { get; set; }
    public string? BinaryPath { get; set; }
    public long? SizeBytes { get; set; }
}

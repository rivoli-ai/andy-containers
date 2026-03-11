namespace Andy.Containers.Models;

public class InstalledPackage
{
    public required string Name { get; set; }
    public required string Version { get; set; }
    public string? Architecture { get; set; }
    public string? Source { get; set; }
}

namespace Andy.Containers.Models;

public class OsInfo
{
    public required string Name { get; set; }
    public required string Version { get; set; }
    public string Codename { get; set; } = "";
    public string KernelVersion { get; set; } = "";
}

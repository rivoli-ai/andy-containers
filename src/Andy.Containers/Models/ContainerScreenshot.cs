namespace Andy.Containers.Models;

public class ContainerScreenshot
{
    public string? AnsiText { get; set; }
    public DateTime CapturedAt { get; set; }
    public int Cols { get; set; } = 120;
    public int Rows { get; set; } = 40;
    public string Source { get; set; } = "tmux";
}

public class ContainerMetadata
{
    public ContainerScreenshot? Screenshot { get; set; }
}

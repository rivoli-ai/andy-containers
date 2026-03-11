namespace Andy.Containers.Models;

public class SshConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 22;
    public List<string> AuthMethods { get; set; } = ["public_key"];
    public bool RootLogin { get; set; }
    public int IdleTimeoutMinutes { get; set; } = 60;
}

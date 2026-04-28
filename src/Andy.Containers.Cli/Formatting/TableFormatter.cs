using Andy.Containers.Client;
using Andy.Containers.Models;
using Spectre.Console;

namespace Andy.Containers.Cli.Formatting;

public static class TableFormatter
{
    public static void PrintContainerTable(ContainersClient.ContainerDto[] containers)
    {
        if (containers.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No containers found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn("Uptime")
            .AddColumn("Provider")
            .AddColumn("ID");

        foreach (var c in containers)
        {
            var statusColor = c.Status switch
            {
                "Running" => "green",
                "Stopped" => "yellow",
                "Creating" or "Pending" => "cyan",
                "Destroyed" or "Destroying" or "Failed" => "red",
                _ => "dim"
            };

            table.AddRow(
                Markup.Escape(c.Name),
                $"[{statusColor}]{c.Status}[/]",
                c.Status == "Running" ? FormatUptime(c.StartedAt) : "[dim]--[/]",
                c.Provider?.Name ?? c.ProviderId?[..8] ?? "--",
                c.Id[..8]
            );
        }

        AnsiConsole.Write(table);
    }

    public static void PrintContainerDetail(ContainersClient.ContainerDto c)
    {
        var panel = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Field")
            .AddColumn("Value")
            .HideHeaders();

        panel.AddRow("Name", $"[bold]{Markup.Escape(c.Name)}[/]");
        panel.AddRow("Status", FormatStatus(c.Status));
        panel.AddRow("ID", $"[dim]{c.Id}[/]");
        if (c.ExternalId is not null)
            panel.AddRow("External ID", $"[dim]{c.ExternalId[..Math.Min(12, c.ExternalId.Length)]}[/]");
        panel.AddRow("Owner", c.OwnerId);
        if (c.Template is not null)
            panel.AddRow("Template", $"{c.Template.Name} ([dim]{c.Template.Code}[/])");
        if (c.Template?.BaseImage is not null)
            panel.AddRow("Base Image", $"[dim]{c.Template.BaseImage}[/]");
        if (c.Provider is not null)
            panel.AddRow("Provider", $"{c.Provider.Name} ({c.Provider.Type})");
        if (c.CreatedAt is not null)
            panel.AddRow("Created", c.CreatedAt);
        if (c.StartedAt is not null)
            panel.AddRow("Started", c.StartedAt);
        if (c.Status == "Running" && c.StartedAt is not null)
            panel.AddRow("Uptime", $"[green]{FormatUptime(c.StartedAt)}[/]");
        if (c.HostIp is not null)
            panel.AddRow("Host IP", c.HostIp);
        if (c.IdeEndpoint is not null)
            panel.AddRow("IDE", $"[link]{c.IdeEndpoint}[/]");
        if (c.VncEndpoint is not null)
            panel.AddRow("VNC", $"[link]{c.VncEndpoint}[/]");

        AnsiConsole.Write(panel);
    }

    public static void PrintStats(ContainersClient.ContainerStatsDto stats)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn("Value")
            .AddColumn("Usage");

        var cpuColor = stats.CpuPercent > 80 ? "red" : stats.CpuPercent > 50 ? "yellow" : "green";
        var memColor = stats.MemoryPercent > 80 ? "red" : stats.MemoryPercent > 50 ? "yellow" : "green";

        table.AddRow("CPU", $"[{cpuColor}]{stats.CpuPercent:F1}%[/]", RenderBar(stats.CpuPercent, cpuColor));
        table.AddRow("Memory",
            $"[{memColor}]{FormatBytes(stats.MemoryUsageBytes)} / {FormatBytes(stats.MemoryLimitBytes)}[/]",
            RenderBar(stats.MemoryPercent, memColor));

        if (stats.DiskUsageBytes > 0)
        {
            table.AddRow("Disk",
                stats.DiskLimitBytes > 0
                    ? $"{FormatBytes(stats.DiskUsageBytes)} / {FormatBytes(stats.DiskLimitBytes)}"
                    : FormatBytes(stats.DiskUsageBytes),
                stats.DiskPercent > 0 ? RenderBar(stats.DiskPercent, "blue") : "");
        }

        AnsiConsole.Write(table);
    }

    private static string FormatStatus(string status) => status switch
    {
        "Running" => "[green]● Running[/]",
        "Stopped" => "[yellow]■ Stopped[/]",
        "Creating" or "Pending" => "[cyan]◌ " + status + "[/]",
        "Destroyed" => "[red]✗ Destroyed[/]",
        _ => $"[dim]{status}[/]"
    };

    private static string RenderBar(double percent, string color)
    {
        var filled = (int)(percent / 5);
        filled = Math.Clamp(filled, 0, 20);
        return $"[{color}]{new string('█', filled)}[/][dim]{new string('░', 20 - filled)}[/] {percent:F1}%";
    }

    public static string FormatUptime(string? startedAt)
    {
        if (startedAt is null) return "--";
        if (!DateTime.TryParse(startedAt, out var start)) return "--";
        var diff = DateTime.UtcNow - start.ToUniversalTime();
        if (diff.TotalSeconds < 0) return "--";
        return FormatDuration(diff);
    }

    private static string FormatDuration(TimeSpan ts)
    {
        var totalDays = (int)ts.TotalDays;
        var years = totalDays / 365;
        var months = totalDays / 30;

        if (years > 0)
        {
            var remMonths = (totalDays - years * 365) / 30;
            return remMonths > 0 ? $"{years}y {remMonths}mo" : $"{years}y";
        }
        if (months > 0)
        {
            var remDays = totalDays - months * 30;
            return remDays > 0 ? $"{months}mo {remDays}d" : $"{months}mo";
        }
        if (totalDays > 0)
        {
            var remHours = ts.Hours;
            return remHours > 0 ? $"{totalDays}d {remHours}h" : $"{totalDays}d";
        }
        if (ts.Hours > 0)
            return ts.Minutes > 0 ? $"{ts.Hours}h {ts.Minutes}m" : $"{ts.Hours}h";
        if (ts.Minutes > 0)
            return $"{ts.Minutes}m";
        return $"{ts.Seconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }

    // X7 (rivoli-ai/andy-containers#97). Environment catalog views.

    public static void PrintEnvironmentTable(IReadOnlyList<EnvironmentProfileDto> profiles)
    {
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No environment profiles found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Code")
            .AddColumn("Display name")
            .AddColumn("Kind")
            .AddColumn("GUI")
            .AddColumn("Secrets")
            .AddColumn("Audit")
            .AddColumn("Base image");

        foreach (var p in profiles)
        {
            var kindColor = p.Kind switch
            {
                "HeadlessContainer" => "cyan",
                "Terminal" => "yellow",
                "Desktop" => "magenta",
                _ => "white",
            };
            table.AddRow(
                Markup.Escape(p.Code),
                Markup.Escape(p.DisplayName),
                $"[{kindColor}]{p.Kind}[/]",
                p.Capabilities.HasGui ? "[green]yes[/]" : "[dim]no[/]",
                Markup.Escape(p.Capabilities.SecretsScope.ToString()),
                AuditModeColor(p.Capabilities.AuditMode),
                Markup.Escape(p.BaseImageRef));
        }

        AnsiConsole.Write(table);
    }

    public static void PrintEnvironmentDetail(EnvironmentProfileDto profile)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .HideHeaders()
            .AddColumn("")
            .AddColumn("");

        table.AddRow("Code", Markup.Escape(profile.Code));
        table.AddRow("Display name", Markup.Escape(profile.DisplayName));
        table.AddRow("Kind", profile.Kind);
        table.AddRow("Base image", Markup.Escape(profile.BaseImageRef));
        table.AddRow("Created", profile.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("GUI", profile.Capabilities.HasGui ? "[green]yes[/]" : "[dim]no[/]");
        table.AddRow("Secrets scope", profile.Capabilities.SecretsScope.ToString());
        table.AddRow("Audit mode", AuditModeColor(profile.Capabilities.AuditMode));
        var allowlist = profile.Capabilities.NetworkAllowlist;
        table.AddRow("Network allowlist",
            allowlist.Count == 0 ? "[dim](none — egress denied)[/]"
                : string.Join(", ", allowlist.Select(Markup.Escape)));

        AnsiConsole.Write(table);
    }

    private static string AuditModeColor(AuditMode mode) => mode switch
    {
        AuditMode.Strict => "[red]Strict[/]",
        AuditMode.Standard => "[yellow]Standard[/]",
        AuditMode.None => "[dim]None[/]",
        _ => mode.ToString(),
    };
}

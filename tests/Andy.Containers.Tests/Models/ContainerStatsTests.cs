using Andy.Containers.Abstractions;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class ContainerStatsTests
{
    [Fact]
    public void NewContainerStats_ShouldHaveDefaultValues()
    {
        var stats = new ContainerStats();

        stats.CpuPercent.Should().Be(0);
        stats.MemoryUsageBytes.Should().Be(0);
        stats.MemoryLimitBytes.Should().Be(0);
        stats.MemoryPercent.Should().Be(0);
        stats.DiskUsageBytes.Should().Be(0);
        stats.DiskLimitBytes.Should().Be(0);
        stats.DiskPercent.Should().Be(0);
    }

    [Fact]
    public void NewContainerStats_ShouldHaveTimestampSet()
    {
        var before = DateTime.UtcNow;
        var stats = new ContainerStats();
        var after = DateTime.UtcNow;

        stats.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void ContainerStats_ShouldStoreAllProperties()
    {
        var timestamp = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var stats = new ContainerStats
        {
            CpuPercent = 45.5,
            MemoryUsageBytes = 512_000_000,
            MemoryLimitBytes = 1_073_741_824,
            MemoryPercent = 47.7,
            DiskUsageBytes = 2_000_000_000,
            DiskLimitBytes = 10_737_418_240,
            DiskPercent = 18.6,
            Timestamp = timestamp
        };

        stats.CpuPercent.Should().Be(45.5);
        stats.MemoryUsageBytes.Should().Be(512_000_000);
        stats.MemoryLimitBytes.Should().Be(1_073_741_824);
        stats.MemoryPercent.Should().Be(47.7);
        stats.DiskUsageBytes.Should().Be(2_000_000_000);
        stats.DiskLimitBytes.Should().Be(10_737_418_240);
        stats.DiskPercent.Should().Be(18.6);
        stats.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void ContainerStats_MemoryPercent_CanRepresentFullUsage()
    {
        var stats = new ContainerStats
        {
            MemoryUsageBytes = 1_073_741_824,
            MemoryLimitBytes = 1_073_741_824,
            MemoryPercent = 100.0
        };

        stats.MemoryPercent.Should().Be(100.0);
        stats.MemoryUsageBytes.Should().Be(stats.MemoryLimitBytes);
    }

    [Fact]
    public void ContainerStats_CpuPercent_CanExceedHundred()
    {
        // CPU percent can exceed 100% on multi-core systems
        var stats = new ContainerStats { CpuPercent = 350.0 };

        stats.CpuPercent.Should().Be(350.0);
    }
}

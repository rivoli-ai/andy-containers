using Andy.Containers.Abstractions;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class CostEstimationService : ICostEstimationService
{
    private const decimal HoursPerMonth = 730m;

    public CostEstimate Estimate(ProviderType type, ResourceSpec resources, string? region = null)
    {
        return type switch
        {
            ProviderType.AzureAci => EstimateAzureAci(resources),
            ProviderType.GcpCloudRun => EstimateGcpCloudRun(resources),
            ProviderType.AwsFargate => EstimateAwsFargate(resources),
            ProviderType.FlyIo => EstimateFlyIo(resources),
            ProviderType.Hetzner => EstimateHetzner(resources),
            ProviderType.DigitalOcean => EstimateDigitalOcean(resources),
            ProviderType.Civo => EstimateCivo(resources),
            ProviderType.Docker or ProviderType.AppleContainer => new CostEstimate
            {
                HourlyCostUsd = 0m,
                MonthlyCostUsd = 0m,
                FreeTierNote = "Local provider — no cloud cost",
                Breakdown = []
            },
            _ => new CostEstimate
            {
                HourlyCostUsd = 0m,
                MonthlyCostUsd = 0m,
                FreeTierNote = "Cost estimation not available for this provider"
            }
        };
    }

    private static CostEstimate EstimateAzureAci(ResourceSpec resources)
    {
        // Azure ACI pricing (East US): vCPU $0.049/hr, Memory $0.0054/hr/GB
        var cpuCostHr = (decimal)resources.CpuCores * 0.049m;
        var memCostHr = (resources.MemoryMb / 1024m) * 0.0054m;
        var hourly = cpuCostHr + memCostHr;

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(hourly * HoursPerMonth, 2),
            Breakdown =
            [
                new CostBreakdown { Component = "vCPU", HourlyCostUsd = Math.Round(cpuCostHr, 4), Unit = "per vCPU-hour" },
                new CostBreakdown { Component = "Memory", HourlyCostUsd = Math.Round(memCostHr, 4), Unit = "per GB-hour" }
            ]
        };
    }

    private static CostEstimate EstimateGcpCloudRun(ResourceSpec resources)
    {
        // GCP Cloud Run always-allocated: vCPU $0.0864/hr, Memory $0.009/hr/GB
        var cpuCostHr = (decimal)resources.CpuCores * 0.0864m;
        var memCostHr = (resources.MemoryMb / 1024m) * 0.009m;
        var hourly = cpuCostHr + memCostHr;

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(hourly * HoursPerMonth, 2),
            FreeTierNote = "Free tier: 2M requests + 360K vCPU-seconds/month",
            Breakdown =
            [
                new CostBreakdown { Component = "vCPU", HourlyCostUsd = Math.Round(cpuCostHr, 4), Unit = "per vCPU-hour" },
                new CostBreakdown { Component = "Memory", HourlyCostUsd = Math.Round(memCostHr, 4), Unit = "per GB-hour" }
            ]
        };
    }

    private static CostEstimate EstimateAwsFargate(ResourceSpec resources)
    {
        // AWS Fargate (us-east-1): vCPU $0.04048/hr, Memory $0.004445/hr/GB
        var cpuCostHr = (decimal)resources.CpuCores * 0.04048m;
        var memCostHr = (resources.MemoryMb / 1024m) * 0.004445m;
        var hourly = cpuCostHr + memCostHr;

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(hourly * HoursPerMonth, 2),
            FreeTierNote = "750 hrs/month free (first 12 months)",
            Breakdown =
            [
                new CostBreakdown { Component = "vCPU", HourlyCostUsd = Math.Round(cpuCostHr, 4), Unit = "per vCPU-hour" },
                new CostBreakdown { Component = "Memory", HourlyCostUsd = Math.Round(memCostHr, 4), Unit = "per GB-hour" }
            ]
        };
    }

    private static CostEstimate EstimateFlyIo(ResourceSpec resources)
    {
        // Fly.io pricing (performance machines): ~$0.0357/hr per vCPU, ~$0.0045/hr per GB
        // shared-cpu: $0.0048/hr
        var isShared = resources.CpuCores <= 1 && resources.MemoryMb <= 512;
        decimal hourly;
        CostBreakdown[] breakdown;

        if (isShared)
        {
            hourly = 0.0048m;
            breakdown = [new CostBreakdown { Component = "shared-cpu-1x", HourlyCostUsd = hourly, Unit = "per machine-hour" }];
        }
        else
        {
            var cpuCostHr = (decimal)resources.CpuCores * 0.0357m;
            var memCostHr = (resources.MemoryMb / 1024m) * 0.0045m;
            hourly = cpuCostHr + memCostHr;
            breakdown =
            [
                new CostBreakdown { Component = "vCPU", HourlyCostUsd = Math.Round(cpuCostHr, 4), Unit = "per vCPU-hour" },
                new CostBreakdown { Component = "Memory", HourlyCostUsd = Math.Round(memCostHr, 4), Unit = "per GB-hour" }
            ];
        }

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(hourly * HoursPerMonth, 2),
            FreeTierNote = "3 shared machines free",
            Breakdown = breakdown
        };
    }

    private static CostEstimate EstimateHetzner(ResourceSpec resources)
    {
        // Hetzner Cloud server pricing (Falkenstein) — flat monthly prices mapped to hourly
        var (_, monthlyCost) = MapHetznerCost(resources.CpuCores, resources.MemoryMb);
        var hourly = monthlyCost / HoursPerMonth;

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(monthlyCost, 2),
            Breakdown =
            [
                new CostBreakdown { Component = "Server", HourlyCostUsd = Math.Round(hourly, 4), Unit = "per server-hour" }
            ]
        };
    }

    private static CostEstimate EstimateDigitalOcean(ResourceSpec resources)
    {
        // DigitalOcean Droplet pricing — flat monthly prices
        var monthlyCost = MapDigitalOceanCost(resources.CpuCores, resources.MemoryMb);
        var hourly = monthlyCost / HoursPerMonth;

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(monthlyCost, 2),
            Breakdown =
            [
                new CostBreakdown { Component = "Droplet", HourlyCostUsd = Math.Round(hourly, 4), Unit = "per droplet-hour" }
            ]
        };
    }

    private static CostEstimate EstimateCivo(ResourceSpec resources)
    {
        // Civo pricing
        var monthlyCost = MapCivoCost(resources.CpuCores, resources.MemoryMb);
        var hourly = monthlyCost / HoursPerMonth;

        return new CostEstimate
        {
            HourlyCostUsd = Math.Round(hourly, 4),
            MonthlyCostUsd = Math.Round(monthlyCost, 2),
            Breakdown =
            [
                new CostBreakdown { Component = "Instance", HourlyCostUsd = Math.Round(hourly, 4), Unit = "per instance-hour" }
            ]
        };
    }

    private static (string serverType, decimal monthlyCostUsd) MapHetznerCost(double cpuCores, int memoryMb)
    {
        return cpuCores switch
        {
            <= 2 when memoryMb <= 4096 => ("CX22", 4.35m),
            <= 4 when memoryMb <= 8192 => ("CX32", 7.69m),
            <= 8 when memoryMb <= 16384 => ("CX42", 14.49m),
            <= 16 when memoryMb <= 32768 => ("CX52", 28.49m),
            _ => ("CX52", 28.49m)
        };
    }

    private static decimal MapDigitalOceanCost(double cpuCores, int memoryMb)
    {
        return cpuCores switch
        {
            <= 1 when memoryMb <= 1024 => 6m,
            <= 1 when memoryMb <= 2048 => 12m,
            <= 2 when memoryMb <= 2048 => 18m,
            <= 2 when memoryMb <= 4096 => 24m,
            <= 4 when memoryMb <= 8192 => 48m,
            <= 8 when memoryMb <= 16384 => 96m,
            _ => 96m
        };
    }

    private static decimal MapCivoCost(double cpuCores, int memoryMb)
    {
        return cpuCores switch
        {
            <= 1 when memoryMb <= 1024 => 5m,
            <= 1 when memoryMb <= 2048 => 10m,
            <= 2 when memoryMb <= 4096 => 20m,
            <= 4 when memoryMb <= 8192 => 40m,
            <= 8 when memoryMb <= 16384 => 80m,
            _ => 80m
        };
    }
}

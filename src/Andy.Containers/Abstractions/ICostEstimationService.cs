using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

public interface ICostEstimationService
{
    CostEstimate Estimate(ProviderType type, ResourceSpec resources, string? region = null);
}

public class CostEstimate
{
    public decimal HourlyCostUsd { get; set; }
    public decimal MonthlyCostUsd { get; set; }
    public string? FreeTierNote { get; set; }
    public CostBreakdown[]? Breakdown { get; set; }
}

public class CostBreakdown
{
    public required string Component { get; set; }
    public decimal HourlyCostUsd { get; set; }
    public string? Unit { get; set; }
}

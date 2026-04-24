using System.ComponentModel.DataAnnotations;

namespace Andy.Containers.Models;

/// <summary>
/// Request body for <c>POST /api/runs</c>. Mirrors the subset of <see cref="Run"/>
/// fields a caller can set at submission time — the entity owns lifecycle fields
/// (<c>Status</c>, <c>StartedAt</c>, etc.) and AP5's mode dispatcher owns
/// <c>ContainerId</c>, so neither lives on the request DTO.
/// </summary>
public class CreateRunRequest
{
    [Required]
    public string AgentId { get; set; } = string.Empty;

    public int? AgentRevision { get; set; }

    [Required]
    public RunMode Mode { get; set; }

    [Required]
    public Guid EnvironmentProfileId { get; set; }

    public WorkspaceRefRequest? WorkspaceRef { get; set; }

    public Guid? PolicyId { get; set; }

    /// <summary>
    /// Optional caller-supplied causation root per ADR-0001. When null the
    /// controller mints a fresh root id so every Run still has a chain anchor.
    /// </summary>
    public Guid? CorrelationId { get; set; }
}

public class WorkspaceRefRequest
{
    public Guid WorkspaceId { get; set; }
    public string? Branch { get; set; }
}

/// <summary>
/// Response body for <c>POST /api/runs</c>, <c>GET /api/runs/{id}</c>, and
/// <c>POST /api/runs/{id}/cancel</c>. Includes the four fields the AP2 spec
/// names explicitly (<c>id</c>, <c>containerId</c>, <c>status</c>, <c>links</c>)
/// plus the rest of the run record so callers don't need a follow-up fetch
/// just to read what they posted.
/// </summary>
public class RunDto
{
    public Guid Id { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public int? AgentRevision { get; set; }
    public RunMode Mode { get; set; }
    public Guid EnvironmentProfileId { get; set; }
    public WorkspaceRefDto WorkspaceRef { get; set; } = new();
    public Guid? PolicyId { get; set; }
    public Guid? ContainerId { get; set; }
    public RunStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public RunLinks Links { get; set; } = new();

    public static RunDto FromEntity(Run run)
    {
        var cancellable = !RunStatusTransitions.IsTerminal(run.Status);
        return new RunDto
        {
            Id = run.Id,
            AgentId = run.AgentId,
            AgentRevision = run.AgentRevision,
            Mode = run.Mode,
            EnvironmentProfileId = run.EnvironmentProfileId,
            WorkspaceRef = new WorkspaceRefDto
            {
                WorkspaceId = run.WorkspaceRef.WorkspaceId,
                Branch = run.WorkspaceRef.Branch,
            },
            PolicyId = run.PolicyId,
            ContainerId = run.ContainerId,
            Status = run.Status,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            ExitCode = run.ExitCode,
            Error = run.Error,
            CorrelationId = run.CorrelationId,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            Links = new RunLinks
            {
                Self = $"/api/runs/{run.Id}",
                Cancel = cancellable ? $"/api/runs/{run.Id}/cancel" : null,
            },
        };
    }
}

public class WorkspaceRefDto
{
    public Guid WorkspaceId { get; set; }
    public string? Branch { get; set; }
}

public class RunLinks
{
    public string Self { get; set; } = string.Empty;

    /// <summary>Null once the run is in a terminal state.</summary>
    public string? Cancel { get; set; }
}

using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#945 (M1.5.3). Runs the code-assistant install
/// script inside a provisioned container and writes the outcome to
/// <see cref="Container.CodeAssistantStatus"/> + reason + timestamp.
///
/// Used by:
/// - <c>ContainerProvisioningWorker</c> right after the container's
///   post-create scripts complete.
/// - <c>ContainersController</c>'s retry-install endpoint when the
///   user clicks "Retry install" on a failed/skipped row.
///
/// Shared so the status semantics (Installed / Failed / Skipped /
/// Installing + reason format) stay identical across both call sites
/// — the UI banner doesn't care which trigger produced the row.
/// </summary>
public interface ICodeAssistantInstallExecutor
{
    /// <summary>
    /// Generate the install script for <paramref name="codeAssistant"/>,
    /// exec it inside <paramref name="container"/>, and mutate the
    /// container row's <c>CodeAssistantStatus*</c> fields accordingly.
    /// The caller is responsible for persisting the mutated container
    /// to its DbContext (so callers can batch the persist with other
    /// changes).
    /// </summary>
    Task RunAsync(Container container, CodeAssistantConfig codeAssistant, CancellationToken ct);
}

using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface ISshProvisioningService
{
    string GenerateSetupScript(SshConfig config, IReadOnlyList<string> publicKeys);
}

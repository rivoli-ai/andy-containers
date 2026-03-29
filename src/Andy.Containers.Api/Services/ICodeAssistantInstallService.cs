using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface ICodeAssistantInstallService
{
    string GenerateInstallScript(CodeAssistantConfig config);
    string GetDefaultApiKeyEnvVar(CodeAssistantType tool);
    string GetDefaultBaseUrlEnvVar(CodeAssistantType tool);
}

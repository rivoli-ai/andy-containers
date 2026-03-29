using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Api.Services;

/// <summary>
/// A policy provider that allows all requests when RBAC is not configured.
/// Any policy name (including [RequirePermission] policies) resolves to a permissive policy.
/// </summary>
public class AllowAllPolicyProvider : DefaultAuthorizationPolicyProvider
{
    private static readonly AuthorizationPolicy AllowAll = new AuthorizationPolicyBuilder()
        .RequireAssertion(_ => true)
        .Build();

    public AllowAllPolicyProvider(IOptions<AuthorizationOptions> options)
        : base(options) { }

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // For any policy (including "Permission:container:read" etc.), return permissive
        return Task.FromResult<AuthorizationPolicy?>(AllowAll);
    }
}

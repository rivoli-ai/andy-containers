namespace Andy.Containers.Api.Services;

/// <summary>
/// Stable, greppable error codes for the image management API. Every
/// 4xx/5xx response from <c>ImagesController</c> /
/// <c>TemplatesController</c> populates one of these. The catalogue
/// mirrors the IM5 OpenAPI <c>ImageManagementError</c> schema and is
/// the contract clients (Conductor's Swift <c>ContainerImagesService</c>,
/// the andy-containers-cli, the MCP gateway) branch on.
/// </summary>
/// <remarks>
/// IM10 (rivoli-ai/andy-containers#264). Adding or renaming a code is
/// a breaking change — bump the API version at the same time, and
/// update <c>docs/api-reference.md</c>.
/// </remarks>
public static class ImageManagementErrors
{
    // 400 — request validation
    public const string TemplateSpecInvalid = "template.spec.invalid";
    public const string TemplateCodeInvalid = "template.code.invalid";

    // 403 — RBAC
    public const string AuthPermissionDenied = "auth.permission_denied";

    // 404 — resource lookups
    public const string TemplateNotFound = "template.not_found";
    public const string ImageNotFound = "image.not_found";
    public const string BuildNotFound = "build.not_found";
    public const string ReferenceNotFound = "reference.not_found";

    // 409 — code-already-in-use with a different spec
    public const string TemplateCodeInUse = "template.code.in-use";

    // 422 — semantic validation / build failures
    public const string TemplateExtendsCycle = "template.extends.cycle";
    public const string TemplateExtendsMissingParent = "template.extends.missing_parent";
    public const string BuildFailed = "build.failed";

    // 503 — engine / registry not configured
    public const string BuildEngineUnavailable = "build.engine.unavailable";
    public const string RegistryNotConfigured = "registry.not_configured";

    // 507 — quota exhausted
    public const string RegistryQuotaExceeded = "registry.quota.exceeded";
}

namespace Andy.Containers.Models;

public static class Permissions
{
    // Image permissions
    public const string ImageCreate = "image:create";
    public const string ImageRead = "image:read";
    public const string ImagePublish = "image:publish";
    public const string ImageDelete = "image:delete";
    public const string ImageBuild = "image:build";

    // Template permissions
    public const string TemplateCreate = "template:create";
    public const string TemplateRead = "template:read";
    public const string TemplatePublish = "template:publish";
    public const string TemplateManage = "template:manage";

    // Provider permissions
    public const string ProviderRead = "provider:read";
    public const string ProviderManage = "provider:manage";

    // API Key permissions
    public const string ApiKeyManage = "api-key:manage";
    public const string ApiKeyAdmin = "api-key:admin";

    // Run permissions (Epic AP). RunsController already gates on these
    // strings; AP8 adds them to the catalog so MCP tools can check via
    // IOrganizationMembershipService and Editor/Viewer roles get the
    // expected default scopes.
    public const string RunWrite = "run:write";
    public const string RunRead = "run:read";
    public const string RunExecute = "run:execute";

    // Environment-profile permissions (Epic X). Catalog is read-only
    // today (X3) — write/manage scopes will land if/when an operator
    // UI requires CRUD on the seeded profiles. EnvironmentRead is
    // granted to every role since the catalog is governance metadata
    // every authenticated user benefits from seeing.
    public const string EnvironmentRead = "environment:read";
}

public static class OrgRoles
{
    public const string Admin = "org:admin";
    public const string Editor = "org:editor";
    public const string Viewer = "org:viewer";

    public static IReadOnlyList<string> GetPermissions(string role) => role switch
    {
        Admin => [
            Permissions.ImageCreate, Permissions.ImageRead, Permissions.ImagePublish,
            Permissions.ImageDelete, Permissions.ImageBuild,
            Permissions.TemplateCreate, Permissions.TemplateRead,
            Permissions.TemplatePublish, Permissions.TemplateManage,
            Permissions.ProviderRead, Permissions.ProviderManage,
            Permissions.ApiKeyManage, Permissions.ApiKeyAdmin,
            Permissions.RunWrite, Permissions.RunRead, Permissions.RunExecute,
            Permissions.EnvironmentRead
        ],
        Editor => [
            Permissions.ImageCreate, Permissions.ImageRead, Permissions.ImageBuild,
            Permissions.TemplateCreate, Permissions.TemplateRead, Permissions.TemplateManage,
            Permissions.ApiKeyManage,
            Permissions.RunWrite, Permissions.RunRead, Permissions.RunExecute,
            Permissions.EnvironmentRead
        ],
        Viewer => [
            Permissions.ImageRead, Permissions.TemplateRead, Permissions.ProviderRead,
            Permissions.ApiKeyManage,
            Permissions.RunRead,
            Permissions.EnvironmentRead
        ],
        _ => []
    };
}

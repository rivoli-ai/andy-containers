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
            Permissions.ProviderRead, Permissions.ProviderManage
        ],
        Editor => [
            Permissions.ImageCreate, Permissions.ImageRead, Permissions.ImageBuild,
            Permissions.TemplateCreate, Permissions.TemplateRead, Permissions.TemplateManage
        ],
        Viewer => [
            Permissions.ImageRead, Permissions.TemplateRead, Permissions.ProviderRead
        ],
        _ => []
    };
}

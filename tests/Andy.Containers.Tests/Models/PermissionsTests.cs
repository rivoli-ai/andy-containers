using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class PermissionsTests
{
    [Fact]
    public void AdminRole_ShouldHaveAllPermissions()
    {
        var permissions = OrgRoles.GetPermissions(OrgRoles.Admin);

        permissions.Should().Contain(Permissions.ImageCreate);
        permissions.Should().Contain(Permissions.ImageRead);
        permissions.Should().Contain(Permissions.ImagePublish);
        permissions.Should().Contain(Permissions.ImageDelete);
        permissions.Should().Contain(Permissions.ImageBuild);
        permissions.Should().Contain(Permissions.TemplateCreate);
        permissions.Should().Contain(Permissions.TemplateRead);
        permissions.Should().Contain(Permissions.TemplatePublish);
        permissions.Should().Contain(Permissions.TemplateManage);
        permissions.Should().Contain(Permissions.ProviderRead);
        permissions.Should().Contain(Permissions.ProviderManage);
    }

    [Fact]
    public void EditorRole_ShouldHaveCreateReadBuildPermissions()
    {
        var permissions = OrgRoles.GetPermissions(OrgRoles.Editor);

        permissions.Should().Contain(Permissions.ImageCreate);
        permissions.Should().Contain(Permissions.ImageRead);
        permissions.Should().Contain(Permissions.ImageBuild);
        permissions.Should().Contain(Permissions.TemplateCreate);
        permissions.Should().Contain(Permissions.TemplateRead);
        permissions.Should().Contain(Permissions.TemplateManage);
    }

    [Fact]
    public void EditorRole_ShouldNotHavePublishDeleteOrProviderManage()
    {
        var permissions = OrgRoles.GetPermissions(OrgRoles.Editor);

        permissions.Should().NotContain(Permissions.ImagePublish);
        permissions.Should().NotContain(Permissions.ImageDelete);
        permissions.Should().NotContain(Permissions.TemplatePublish);
        permissions.Should().NotContain(Permissions.ProviderRead);
        permissions.Should().NotContain(Permissions.ProviderManage);
    }

    [Fact]
    public void ViewerRole_ShouldHaveReadOnlyPermissions()
    {
        var permissions = OrgRoles.GetPermissions(OrgRoles.Viewer);

        permissions.Should().Contain(Permissions.ImageRead);
        permissions.Should().Contain(Permissions.TemplateRead);
        permissions.Should().Contain(Permissions.ProviderRead);
    }

    [Fact]
    public void ViewerRole_ShouldNotHaveWritePermissions()
    {
        var permissions = OrgRoles.GetPermissions(OrgRoles.Viewer);

        permissions.Should().NotContain(Permissions.ImageCreate);
        permissions.Should().NotContain(Permissions.ImageBuild);
        permissions.Should().NotContain(Permissions.ImagePublish);
        permissions.Should().NotContain(Permissions.ImageDelete);
        permissions.Should().NotContain(Permissions.TemplateCreate);
        permissions.Should().NotContain(Permissions.TemplateManage);
        permissions.Should().NotContain(Permissions.ProviderManage);
    }

    [Fact]
    public void UnknownRole_ShouldReturnEmptyPermissions()
    {
        var permissions = OrgRoles.GetPermissions("unknown-role");

        permissions.Should().BeEmpty();
    }

    [Fact]
    public void RoleConstants_ShouldHaveExpectedValues()
    {
        OrgRoles.Admin.Should().Be("org:admin");
        OrgRoles.Editor.Should().Be("org:editor");
        OrgRoles.Viewer.Should().Be("org:viewer");
    }

    [Fact]
    public void PermissionConstants_ShouldFollowNamingConvention()
    {
        Permissions.ImageCreate.Should().Be("image:create");
        Permissions.ImageRead.Should().Be("image:read");
        Permissions.ImagePublish.Should().Be("image:publish");
        Permissions.ImageDelete.Should().Be("image:delete");
        Permissions.ImageBuild.Should().Be("image:build");
        Permissions.TemplateCreate.Should().Be("template:create");
        Permissions.TemplateRead.Should().Be("template:read");
        Permissions.TemplatePublish.Should().Be("template:publish");
        Permissions.TemplateManage.Should().Be("template:manage");
        Permissions.ProviderRead.Should().Be("provider:read");
        Permissions.ProviderManage.Should().Be("provider:manage");
    }
}

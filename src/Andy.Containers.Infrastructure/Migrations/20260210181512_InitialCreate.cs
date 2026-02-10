using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ConnectionConfig = table.Column<string>(type: "jsonb", nullable: true),
                    Capabilities = table.Column<string>(type: "jsonb", nullable: true),
                    Region = table.Column<string>(type: "text", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    HealthStatus = table.Column<int>(type: "integer", nullable: false),
                    LastHealthCheck = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: false),
                    BaseImage = table.Column<string>(type: "text", nullable: false),
                    CatalogScope = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<string>(type: "text", nullable: true),
                    Toolchains = table.Column<string>(type: "jsonb", nullable: true),
                    IdeType = table.Column<int>(type: "integer", nullable: false),
                    DefaultResources = table.Column<string>(type: "jsonb", nullable: true),
                    GpuRequired = table.Column<bool>(type: "boolean", nullable: false),
                    GpuPreferred = table.Column<bool>(type: "boolean", nullable: false),
                    EnvironmentVariables = table.Column<string>(type: "jsonb", nullable: true),
                    Ports = table.Column<string>(type: "jsonb", nullable: true),
                    Scripts = table.Column<string>(type: "jsonb", nullable: true),
                    Tags = table.Column<string[]>(type: "text[]", nullable: true),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    ParentTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Templates_Templates_ParentTemplateId",
                        column: x => x.ParentTemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Containers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocatedResources = table.Column<string>(type: "jsonb", nullable: true),
                    NetworkConfig = table.Column<string>(type: "jsonb", nullable: true),
                    IdeEndpoint = table.Column<string>(type: "text", nullable: true),
                    VncEndpoint = table.Column<string>(type: "text", nullable: true),
                    GitRepository = table.Column<string>(type: "jsonb", nullable: true),
                    EnvironmentVariables = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Containers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Containers_Providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Containers_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DependencySpecs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Ecosystem = table.Column<string>(type: "text", nullable: true),
                    VersionConstraint = table.Column<string>(type: "text", nullable: false),
                    AutoUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatePolicy = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencySpecs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DependencySpecs_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Tag = table.Column<string>(type: "text", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageReference = table.Column<string>(type: "text", nullable: false),
                    BaseImageDigest = table.Column<string>(type: "text", nullable: false),
                    DependencyManifest = table.Column<string>(type: "jsonb", nullable: false),
                    DependencyLock = table.Column<string>(type: "jsonb", nullable: false),
                    BuildNumber = table.Column<int>(type: "integer", nullable: false),
                    BuildStatus = table.Column<int>(type: "integer", nullable: false),
                    BuildLog = table.Column<string>(type: "text", nullable: true),
                    BuildStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BuildCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImageSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    BuiltOffline = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Changelog = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Images_Images_PreviousImageId",
                        column: x => x.PreviousImageId,
                        principalTable: "Images",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Images_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "jsonb", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "Containers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<string>(type: "text", nullable: false),
                    SessionType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EndpointUrl = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "Containers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DefaultContainerId = table.Column<Guid>(type: "uuid", nullable: true),
                    GitRepositoryUrl = table.Column<string>(type: "text", nullable: true),
                    GitBranch = table.Column<string>(type: "text", nullable: true),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAccessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workspaces_Containers_DefaultContainerId",
                        column: x => x.DefaultContainerId,
                        principalTable: "Containers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ResolvedDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependencySpecId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedVersion = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: true),
                    ArtifactHash = table.Column<string>(type: "text", nullable: true),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    FromOfflineCache = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolvedDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResolvedDependencies_DependencySpecs_DependencySpecId",
                        column: x => x.DependencySpecId,
                        principalTable: "DependencySpecs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResolvedDependencies_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContainerWorkspace",
                columns: table => new
                {
                    ContainersId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerWorkspace", x => new { x.ContainersId, x.WorkspaceId });
                    table.ForeignKey(
                        name: "FK_ContainerWorkspace_Containers_ContainersId",
                        column: x => x.ContainersId,
                        principalTable: "Containers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContainerWorkspace_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Containers_OrganizationId",
                table: "Containers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_OwnerId",
                table: "Containers",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_ProviderId",
                table: "Containers",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_Status",
                table: "Containers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_TemplateId",
                table: "Containers",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerWorkspace_WorkspaceId",
                table: "ContainerWorkspace",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_DependencySpecs_TemplateId_Name",
                table: "DependencySpecs",
                columns: new[] { "TemplateId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_ContainerId",
                table: "Events",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Timestamp",
                table: "Events",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Images_ContentHash",
                table: "Images",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Images_PreviousImageId",
                table: "Images",
                column: "PreviousImageId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_Tag",
                table: "Images",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_Images_TemplateId",
                table: "Images",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Providers_Code",
                table: "Providers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResolvedDependencies_DependencySpecId",
                table: "ResolvedDependencies",
                column: "DependencySpecId");

            migrationBuilder.CreateIndex(
                name: "IX_ResolvedDependencies_ImageId",
                table: "ResolvedDependencies",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ContainerId",
                table: "Sessions",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_SubjectId",
                table: "Sessions",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_CatalogScope",
                table: "Templates",
                column: "CatalogScope");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_Code",
                table: "Templates",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Templates_ParentTemplateId",
                table: "Templates",
                column: "ParentTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_DefaultContainerId",
                table: "Workspaces",
                column: "DefaultContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_OrganizationId",
                table: "Workspaces",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_OwnerId",
                table: "Workspaces",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerWorkspace");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "ResolvedDependencies");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "DependencySpecs");

            migrationBuilder.DropTable(
                name: "Images");

            migrationBuilder.DropTable(
                name: "Containers");

            migrationBuilder.DropTable(
                name: "Providers");

            migrationBuilder.DropTable(
                name: "Templates");
        }
    }
}

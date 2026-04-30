using Andy.Containers.Messaging;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Infrastructure.Data;

public class ContainersDbContext : DbContext
{
    public ContainersDbContext(DbContextOptions<ContainersDbContext> options) : base(options) { }

    public DbSet<Container> Containers => Set<Container>();
    public DbSet<ContainerTemplate> Templates => Set<ContainerTemplate>();
    public DbSet<ContainerImage> Images => Set<ContainerImage>();
    public DbSet<ContainerSession> Sessions => Set<ContainerSession>();
    public DbSet<ContainerEvent> Events => Set<ContainerEvent>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<InfrastructureProvider> Providers => Set<InfrastructureProvider>();
    public DbSet<DependencySpec> DependencySpecs => Set<DependencySpec>();
    public DbSet<ResolvedDependency> ResolvedDependencies => Set<ResolvedDependency>();
    public DbSet<ContainerGitRepository> ContainerGitRepositories => Set<ContainerGitRepository>();
    public DbSet<GitCredential> GitCredentials => Set<GitCredential>();
    public DbSet<ApiKeyCredential> ApiKeyCredentials => Set<ApiKeyCredential>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<ImageBuildRecord> ImageBuildRecords => Set<ImageBuildRecord>();
    public DbSet<OutboxEntry> OutboxEntries => Set<OutboxEntry>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<EnvironmentProfile> EnvironmentProfiles => Set<EnvironmentProfile>();
    public DbSet<Theme> Themes => Set<Theme>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Provider-specific column type for the jsonb columns. Postgres
        // uses native `jsonb`; SQLite stores the same payload as TEXT
        // (the values themselves are typed CLR objects that EF Core
        // already serialises via its default JSON value converter).
        // This keeps the hosted (Npgsql) deployments unchanged while
        // letting the embedded SQLite path round-trip the same payloads
        // without any schema-level provider awareness leaking out.
        var jsonColumnType = Database.IsNpgsql() ? "jsonb" : null;

        // Container
        modelBuilder.Entity<Container>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.OwnerId);
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.OrganizationId);
            if (jsonColumnType != null)
            {
                e.Property(c => c.AllocatedResources).HasColumnType(jsonColumnType);
                e.Property(c => c.NetworkConfig).HasColumnType(jsonColumnType);
                e.Property(c => c.GitRepository).HasColumnType(jsonColumnType);
                e.Property(c => c.EnvironmentVariables).HasColumnType(jsonColumnType);
                e.Property(c => c.CodeAssistant).HasColumnType(jsonColumnType);
                e.Property(c => c.Metadata).HasColumnType(jsonColumnType);
            }
            e.HasOne(c => c.Template).WithMany().HasForeignKey(c => c.TemplateId);
            e.HasOne(c => c.Provider).WithMany().HasForeignKey(c => c.ProviderId);
            e.HasMany(c => c.Sessions).WithOne(s => s.Container).HasForeignKey(s => s.ContainerId);
            e.HasMany(c => c.Events).WithOne(ev => ev.Container).HasForeignKey(ev => ev.ContainerId);
            e.HasMany(c => c.GitRepositories).WithOne(r => r.Container).HasForeignKey(r => r.ContainerId);
        });

        // ContainerTemplate
        modelBuilder.Entity<ContainerTemplate>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Code).IsUnique();
            e.HasIndex(t => t.CatalogScope);
            if (jsonColumnType != null)
            {
                e.Property(t => t.Toolchains).HasColumnType(jsonColumnType);
                e.Property(t => t.DefaultResources).HasColumnType(jsonColumnType);
                e.Property(t => t.EnvironmentVariables).HasColumnType(jsonColumnType);
                e.Property(t => t.Ports).HasColumnType(jsonColumnType);
                e.Property(t => t.Scripts).HasColumnType(jsonColumnType);
                e.Property(t => t.GitRepositories).HasColumnType(jsonColumnType);
                e.Property(t => t.CodeAssistant).HasColumnType(jsonColumnType);
                e.Property(t => t.Metadata).HasColumnType(jsonColumnType);
            }
            e.HasOne(t => t.ParentTemplate).WithMany().HasForeignKey(t => t.ParentTemplateId);
        });

        // ContainerImage
        modelBuilder.Entity<ContainerImage>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.ContentHash).IsUnique();
            e.HasIndex(i => i.Tag);
            if (jsonColumnType != null)
            {
                e.Property(i => i.DependencyManifest).HasColumnType(jsonColumnType);
                e.Property(i => i.DependencyLock).HasColumnType(jsonColumnType);
                e.Property(i => i.Metadata).HasColumnType(jsonColumnType);
            }
            e.HasOne(i => i.Template).WithMany().HasForeignKey(i => i.TemplateId);
            e.HasOne(i => i.PreviousImage).WithMany().HasForeignKey(i => i.PreviousImageId);
            e.HasIndex(i => new { i.TemplateId, i.OrganizationId });
            e.HasIndex(i => i.OrganizationId);
        });

        // ContainerSession
        modelBuilder.Entity<ContainerSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.ContainerId);
            e.HasIndex(s => s.SubjectId);
            if (jsonColumnType != null)
            {
                e.Property(s => s.Metadata).HasColumnType(jsonColumnType);
            }
        });

        // ContainerEvent
        modelBuilder.Entity<ContainerEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.HasIndex(ev => ev.ContainerId);
            e.HasIndex(ev => ev.Timestamp);
            if (jsonColumnType != null)
            {
                e.Property(ev => ev.Details).HasColumnType(jsonColumnType);
            }
        });

        // Workspace
        modelBuilder.Entity<Workspace>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.OwnerId);
            e.HasIndex(w => w.OrganizationId);
            if (jsonColumnType != null)
            {
                e.Property(w => w.GitRepositories).HasColumnType(jsonColumnType);
                e.Property(w => w.Configuration).HasColumnType(jsonColumnType);
                e.Property(w => w.Metadata).HasColumnType(jsonColumnType);
            }
            e.HasOne(w => w.DefaultContainer).WithMany().HasForeignKey(w => w.DefaultContainerId);
            e.HasMany(w => w.Containers).WithMany();
            // X5 (rivoli-ai/andy-containers#94). Governance binding to the
            // EnvironmentProfile catalog. SetNull on profile-delete so a
            // profile retirement doesn't cascade-destroy workspaces; the
            // workspace stays addressable but loses its image/sidecar
            // derivation until an operator rebinds it.
            e.HasOne(w => w.EnvironmentProfile)
                .WithMany()
                .HasForeignKey(w => w.EnvironmentProfileId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(w => w.EnvironmentProfileId);
        });

        // InfrastructureProvider
        modelBuilder.Entity<InfrastructureProvider>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Code).IsUnique();
            if (jsonColumnType != null)
            {
                e.Property(p => p.ConnectionConfig).HasColumnType(jsonColumnType);
                e.Property(p => p.Capabilities).HasColumnType(jsonColumnType);
                e.Property(p => p.Metadata).HasColumnType(jsonColumnType);
            }
        });

        // DependencySpec
        modelBuilder.Entity<DependencySpec>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.TemplateId, d.Name }).IsUnique();
            if (jsonColumnType != null)
            {
                e.Property(d => d.Metadata).HasColumnType(jsonColumnType);
            }
            e.HasOne(d => d.Template).WithMany().HasForeignKey(d => d.TemplateId);
        });

        // ResolvedDependency
        modelBuilder.Entity<ResolvedDependency>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.ImageId);
            e.HasOne(r => r.Image).WithMany().HasForeignKey(r => r.ImageId);
            e.HasOne(r => r.DependencySpec).WithMany().HasForeignKey(r => r.DependencySpecId);
        });

        // ContainerGitRepository
        modelBuilder.Entity<ContainerGitRepository>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.ContainerId);
            e.HasIndex(r => r.CloneStatus);
            if (jsonColumnType != null)
            {
                e.Property(r => r.CloneMetadata).HasColumnType(jsonColumnType);
            }
        });

        // GitCredential
        modelBuilder.Entity<GitCredential>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.OwnerId);
            e.HasIndex(c => new { c.OwnerId, c.Label }).IsUnique();
        });

        // ApiKeyCredential
        modelBuilder.Entity<ApiKeyCredential>(e =>
        {
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.OwnerId);
            e.HasIndex(k => new { k.OwnerId, k.Provider, k.Label }).IsUnique();
            if (jsonColumnType != null)
            {
                e.Property(k => k.ChangeHistory).HasColumnType(jsonColumnType);
            }
        });

        // Organization
        modelBuilder.Entity<Organization>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.OwnerId);
            e.HasIndex(o => o.Name);
            e.HasMany(o => o.Teams)
                .WithOne(t => t.Organization)
                .HasForeignKey(t => t.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Team
        modelBuilder.Entity<Team>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.OrganizationId);
            e.HasIndex(t => new { t.OrganizationId, t.Name }).IsUnique();
        });

        // ImageBuildRecord
        modelBuilder.Entity<ImageBuildRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.ImageReference);
            e.HasIndex(r => r.TemplateCode).IsUnique();
        });

        // OutboxEntry — transactional outbox rows for messaging (ADR 0001).
        // The dispatcher polls on PublishedAt IS NULL ordered by CreatedAt,
        // so the composite index covers its hot path. Subject gets its own
        // index for ad-hoc diagnostics (e.g. "show me everything on
        // andy.containers.events.run.*").
        modelBuilder.Entity<OutboxEntry>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Subject).IsRequired().HasMaxLength(256);
            e.Property(o => o.PayloadJson).IsRequired();
            e.HasIndex(o => new { o.PublishedAt, o.CreatedAt });
            e.HasIndex(o => o.Subject);
        });

        // Run — agent-run execution entity (AP1, rivoli-ai/andy-containers#103).
        // A Run represents one invocation of an Agent (from andy-agents) against
        // a delegation contract inside a container. Hot paths: "list active runs"
        // (indexed on Status) and causation tracing (indexed on CorrelationId).
        modelBuilder.Entity<Run>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.AgentId).IsRequired().HasMaxLength(100);
            e.Property(r => r.Error).HasMaxLength(4000);
            // Enums as strings for stability across migrations + readability in
            // database tools. Matches the existing pattern elsewhere in the
            // schema (status columns are already string-typed in Container etc.
            // via EF's default enum-to-int mapping — Run is explicit here so
            // debugging via psql/sqlite-cli is friction-free from day one).
            e.Property(r => r.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.CorrelationId);
            e.HasIndex(r => r.AgentId);
            // WorkspaceRef as an owned value object — inlined columns prefixed
            // `WorkspaceRef_*` (EF default). Keeps Run self-contained without
            // introducing a separate table.
            e.OwnsOne(r => r.WorkspaceRef, wr =>
            {
                wr.Property(w => w.WorkspaceId).HasColumnName("WorkspaceRef_WorkspaceId");
                wr.Property(w => w.Branch).HasColumnName("WorkspaceRef_Branch").HasMaxLength(200);
            });
        });

        // EnvironmentProfile — catalog of runtime shapes (X1, rivoli-ai/andy-containers#90).
        // Capabilities is stored as JSON via OwnsOne(...).ToJson() so the envelope can
        // grow without a per-field migration; Postgres uses native jsonb, SQLite stores
        // it as TEXT — both round-trip the same CLR object.
        modelBuilder.Entity<EnvironmentProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(100);
            e.Property(p => p.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(p => p.BaseImageRef).IsRequired().HasMaxLength(500);
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(p => p.Name).IsUnique();
            e.OwnsOne(p => p.Capabilities, c =>
            {
                c.ToJson();
                c.Property(x => x.SecretsScope).HasConversion<string>();
                c.Property(x => x.AuditMode).HasConversion<string>();
            });
        });

        // Theme — predefined visual catalog (Conductor #886).
        // String primary key matches the YAML-seeded slug ("dracula",
        // "github-dark", …) so cross-deploy ids stay predictable. The
        // palette is JSON-encoded as TEXT so the catalog is portable
        // between Postgres and the embedded-SQLite host without
        // dialect-aware schema. Deletion of a referenced theme falls
        // through as ON DELETE SET NULL on the FK columns below — the
        // container/template stays alive, picker just reverts to
        // resolution fallback.
        modelBuilder.Entity<Theme>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).IsRequired().HasMaxLength(64);
            e.Property(t => t.Name).IsRequired().HasMaxLength(64);
            e.Property(t => t.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(t => t.Kind).IsRequired().HasMaxLength(32);
            e.Property(t => t.PaletteJson).IsRequired();
            e.HasIndex(t => t.Name).IsUnique();
            e.HasIndex(t => t.Kind);
        });

        // Container.ThemeId / ContainerTemplate.ThemeId → Theme.Id
        // FK with ON DELETE SET NULL (the container is independent
        // of the catalog row's lifetime).
        modelBuilder.Entity<Container>()
            .HasOne<Theme>()
            .WithMany()
            .HasForeignKey(c => c.ThemeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ContainerTemplate>()
            .HasOne<Theme>()
            .WithMany()
            .HasForeignKey(t => t.ThemeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

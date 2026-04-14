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
    }
}

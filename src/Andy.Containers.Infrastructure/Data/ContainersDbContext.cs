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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Container
        modelBuilder.Entity<Container>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.OwnerId);
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.OrganizationId);
            e.Property(c => c.AllocatedResources).HasColumnType("jsonb");
            e.Property(c => c.NetworkConfig).HasColumnType("jsonb");
            e.Property(c => c.GitRepository).HasColumnType("jsonb");
            e.Property(c => c.EnvironmentVariables).HasColumnType("jsonb");
            e.Property(c => c.Metadata).HasColumnType("jsonb");
            e.HasOne(c => c.Template).WithMany().HasForeignKey(c => c.TemplateId);
            e.HasOne(c => c.Provider).WithMany().HasForeignKey(c => c.ProviderId);
            e.HasMany(c => c.Sessions).WithOne(s => s.Container).HasForeignKey(s => s.ContainerId);
            e.HasMany(c => c.Events).WithOne(ev => ev.Container).HasForeignKey(ev => ev.ContainerId);
        });

        // ContainerTemplate
        modelBuilder.Entity<ContainerTemplate>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Code).IsUnique();
            e.HasIndex(t => t.CatalogScope);
            e.Property(t => t.Toolchains).HasColumnType("jsonb");
            e.Property(t => t.DefaultResources).HasColumnType("jsonb");
            e.Property(t => t.EnvironmentVariables).HasColumnType("jsonb");
            e.Property(t => t.Ports).HasColumnType("jsonb");
            e.Property(t => t.Scripts).HasColumnType("jsonb");
            e.Property(t => t.Metadata).HasColumnType("jsonb");
            e.HasOne(t => t.ParentTemplate).WithMany().HasForeignKey(t => t.ParentTemplateId);
        });

        // ContainerImage
        modelBuilder.Entity<ContainerImage>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.ContentHash).IsUnique();
            e.HasIndex(i => i.Tag);
            e.Property(i => i.DependencyManifest).HasColumnType("jsonb");
            e.Property(i => i.DependencyLock).HasColumnType("jsonb");
            e.Property(i => i.Metadata).HasColumnType("jsonb");
            e.HasOne(i => i.Template).WithMany().HasForeignKey(i => i.TemplateId);
            e.HasOne(i => i.PreviousImage).WithMany().HasForeignKey(i => i.PreviousImageId);
        });

        // ContainerSession
        modelBuilder.Entity<ContainerSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.ContainerId);
            e.HasIndex(s => s.SubjectId);
            e.Property(s => s.Metadata).HasColumnType("jsonb");
        });

        // ContainerEvent
        modelBuilder.Entity<ContainerEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.HasIndex(ev => ev.ContainerId);
            e.HasIndex(ev => ev.Timestamp);
            e.Property(ev => ev.Details).HasColumnType("jsonb");
        });

        // Workspace
        modelBuilder.Entity<Workspace>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.OwnerId);
            e.HasIndex(w => w.OrganizationId);
            e.Property(w => w.Configuration).HasColumnType("jsonb");
            e.Property(w => w.Metadata).HasColumnType("jsonb");
            e.HasOne(w => w.DefaultContainer).WithMany().HasForeignKey(w => w.DefaultContainerId);
            e.HasMany(w => w.Containers).WithMany();
        });

        // InfrastructureProvider
        modelBuilder.Entity<InfrastructureProvider>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.ConnectionConfig).HasColumnType("jsonb");
            e.Property(p => p.Capabilities).HasColumnType("jsonb");
            e.Property(p => p.Metadata).HasColumnType("jsonb");
        });

        // DependencySpec
        modelBuilder.Entity<DependencySpec>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.TemplateId, d.Name }).IsUnique();
            e.Property(d => d.Metadata).HasColumnType("jsonb");
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
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.ContainerId);
            e.HasIndex(g => new { g.ContainerId, g.TargetPath }).IsUnique();
        });

        // GitCredential
        modelBuilder.Entity<GitCredential>(e =>
        {
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.UserId);
            e.HasIndex(g => new { g.UserId, g.Label }).IsUnique();
        });
    }
}

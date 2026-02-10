using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Andy.Containers.Infrastructure.Data;

public class ContainersDbContextFactory : IDesignTimeDbContextFactory<ContainersDbContext>
{
    public ContainersDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContainersDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5434;Database=andy_containers;Username=postgres;Password=postgres");
        return new ContainersDbContext(optionsBuilder.Options);
    }
}

using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Tests.Helpers;

public static class InMemoryDbHelper
{
    public static ContainersDbContext CreateContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;

        var context = new ContainersDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}

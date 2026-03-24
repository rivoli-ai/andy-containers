using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    public static IServiceScopeFactory CreateScopeFactory(ContainersDbContext db)
    {
        // Register the db context as a singleton so it doesn't get disposed with the scope
        var serviceProvider = new ServiceCollection()
            .AddSingleton<ContainersDbContext>(_ => db)
            .BuildServiceProvider();

        return serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }
}

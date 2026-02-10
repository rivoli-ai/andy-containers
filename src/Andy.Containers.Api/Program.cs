using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Text.Json.Serialization;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Logging
    builder.Host.UseSerilog();

    // Swagger
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Andy Containers API", Version = "v1" });
    });

    // Controllers
    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

    // Database
    builder.Services.AddDbContext<ContainersDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

    // Services
    builder.Services.AddScoped<IContainerService, ContainerOrchestrationService>();
    builder.Services.AddScoped<IInfrastructureRoutingService, InfrastructureRoutingService>();
    builder.Services.AddSingleton<IInfrastructureProviderFactory, InfrastructureProviderFactory>();

    // MCP
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    // Authentication — disabled in dev for easy testing
    builder.Services.AddAuthentication("Bearer")
        .AddJwtBearer("Bearer", options =>
        {
            options.Authority = builder.Configuration["Auth:Authority"];
            options.Audience = builder.Configuration["Auth:Audience"];
            options.RequireHttpsMetadata = true;
        });
    builder.Services.AddAuthorization();

    // Health checks
    builder.Services.AddHealthChecks();

    // CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
                ?? ["https://localhost:5280", "https://localhost:3000"];
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });

    var app = builder.Build();

    // Auto-migrate and seed
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        await DataSeeder.SeedAsync(db);
    }

    app.UseHttpsRedirection();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors();

    // MCP endpoint
    app.MapMcp("/mcp");

    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/health");

    Log.Information("Andy Containers API starting");
    Log.Information("Swagger UI: https://localhost:5200/swagger");
    Log.Information("MCP endpoint: https://localhost:5200/mcp");
    Log.Information("Health: https://localhost:5200/health");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Andy Containers API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

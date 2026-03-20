using System.Security.Claims;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Andy.Containers.Api.Telemetry;
using Serilog;
using System.Text.Json.Serialization;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Logging
    builder.Host.UseSerilog((context, config) =>
    {
        config.WriteTo.Console();
        var otlpEndpoint = context.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            config.WriteTo.OpenTelemetry(o =>
            {
                o.Endpoint = otlpEndpoint;
                o.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = context.Configuration["OpenTelemetry:ServiceName"] ?? "andy-containers-api"
                };
            });
        }
    });

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
    builder.Services.AddSingleton<ICostEstimationService, CostEstimationService>();
    builder.Services.AddSingleton<IYamlTemplateParser, YamlTemplateParser>();

    // Container provisioning queue + background worker
    builder.Services.AddSingleton<ContainerProvisioningQueue>();
    builder.Services.AddHostedService<ContainerProvisioningWorker>();

    // Git credential + clone services
    builder.Services.AddDataProtection();
    builder.Services.AddScoped<IGitCredentialService, GitCredentialService>();
    builder.Services.AddScoped<IGitCloneService, GitCloneService>();
    builder.Services.AddScoped<IToolVersionDetector, ToolVersionDetector>();
    builder.Services.AddScoped<IImageManifestService, ImageManifestService>();
    builder.Services.AddScoped<IImageDiffService, ImageDiffService>();

    // Current user service for RBAC
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

    // MCP
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    // Authentication
    builder.Services.AddAuthentication("Bearer")
        .AddJwtBearer("Bearer", options =>
        {
            options.Authority = builder.Configuration["Auth:Authority"];
            options.Audience = builder.Configuration["Auth:Audience"];
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Admin", policy => policy.RequireClaim("role", "admin"));
    });

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

    // OpenTelemetry
    builder.Services.AddAndyTelemetry(builder.Configuration);

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

    // Dev mode: assign a default identity when no real auth provider is running
    if (app.Environment.IsDevelopment())
    {
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                var devUserId = app.Configuration["Auth:DevUserId"] ?? "dev-user";
                var devEmail = app.Configuration["Auth:DevEmail"] ?? "dev@andy.local";
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, devUserId),
                    new Claim("sub", devUserId),
                    new Claim(ClaimTypes.Email, devEmail),
                    new Claim("email", devEmail),
                    new Claim(ClaimTypes.Name, "Dev User"),
                    new Claim("name", "Dev User"),
                    new Claim(ClaimTypes.Role, "admin"),
                    new Claim("role", "admin")
                };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Development"));
            }
            await next();
        });
    }

    app.UseAuthorization();
    app.MapControllers().RequireAuthorization();
    app.MapHealthChecks("/health").AllowAnonymous();

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

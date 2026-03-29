using System.Security.Claims;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Andy.Containers.Api.Telemetry;
using Andy.Rbac.Client;
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

    // Provider health check background worker
    builder.Services.AddHostedService<ProviderHealthCheckWorker>();

    // Container status sync worker — periodically checks running containers against provider
    builder.Services.AddHostedService<ContainerStatusSyncWorker>();

    // Git credential + clone services
    builder.Services.AddDataProtection();
    builder.Services.AddScoped<IGitCredentialService, GitCredentialService>();
    builder.Services.AddScoped<IGitCloneService, GitCloneService>();
    builder.Services.AddScoped<IGitRepositoryProbeService, GitRepositoryProbeService>();
    builder.Services.AddSingleton<ICodeAssistantInstallService, CodeAssistantInstallService>();
    builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
    builder.Services.AddScoped<IApiKeyValidationService, ApiKeyValidationService>();
    builder.Services.AddHttpClient("ApiKeyValidation");
    builder.Services.AddScoped<IToolVersionDetector, ToolVersionDetector>();
    builder.Services.AddScoped<IImageManifestService, ImageManifestService>();
    builder.Services.AddScoped<IImageDiffService, ImageDiffService>();

    // Current user service for RBAC
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

    // Organization RBAC
    builder.Services.AddMemoryCache();
    builder.Services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
    builder.Services.AddScoped<IContainerAuthorizationService, ContainerAuthorizationService>();
    var orgRbacUrl = builder.Configuration["Rbac:ApiBaseUrl"] ?? "";
    if (!string.IsNullOrEmpty(orgRbacUrl))
    {
        builder.Services.AddHttpClient("AndyRbac", client =>
        {
            client.BaseAddress = new Uri(orgRbacUrl);
        });
    }

    // MCP
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    // Authentication
    var authority = builder.Configuration["AndyAuth:Authority"] ?? "";
    if (string.IsNullOrEmpty(authority))
    {
        // No authority configured — permissive fallback for local dev
        builder.Services.AddAuthentication("Bearer")
            .AddJwtBearer("Bearer", options =>
            {
                options.RequireHttpsMetadata = false;
            });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAssertion(_ => true)
                .Build();
            options.AddPolicy("Admin", policy => policy.RequireClaim("role", "admin"));
        });
    }
    else
    {
        builder.Services.AddAuthentication("Bearer")
            .AddJwtBearer("Bearer", options =>
            {
                options.Authority = authority;
                options.Audience = builder.Configuration["AndyAuth:Audience"];
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy => policy.RequireClaim("role", "admin"));
        });
    }

    // RBAC client
    var rbacBaseUrl = builder.Configuration["Rbac:ApiBaseUrl"] ?? "";
    if (!string.IsNullOrEmpty(rbacBaseUrl))
    {
        builder.Services.AddRbacClient(options =>
        {
            options.ApiBaseUrl = rbacBaseUrl;
            options.ApplicationCode = "containers";
        });
    }
    else
    {
        throw new InvalidOperationException(
            "Rbac:ApiBaseUrl is not configured. RBAC is required — set the URL in appsettings.json or appsettings.Development.json.");
    }

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

    // Auto-create DB (if missing) and seed
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
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
                var devUserId = app.Configuration["AndyAuth:DevUserId"] ?? "dev-user";
                var devEmail = app.Configuration["AndyAuth:DevEmail"] ?? "dev@andy.local";
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

    app.UseWebSockets();
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

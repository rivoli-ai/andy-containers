using System.Security.Claims;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Andy.Containers.Api.Telemetry;
using Andy.Rbac.Client;
using Andy.Settings.Client;
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

    // Database — provider switch (PostgreSQL for hosted/Docker, SQLite
    // for embedded Conductor). Active provider read from
    // `Database:Provider` config key. `appsettings.json` pins PostgreSql
    // so existing deployment paths are unchanged; Conductor's embedded
    // launcher overrides via `Database__Provider=Sqlite` env var.
    var dbProvider = DatabaseProviderExtensions.GetDatabaseProvider(builder.Configuration);
    var dbConnectionString = DatabaseProviderExtensions.ResolveConnectionString(builder.Configuration, dbProvider);
    builder.Services.AddDbContext<ContainersDbContext>(options =>
    {
        DatabaseProviderExtensions.ConfigureDbContext(options, dbProvider, dbConnectionString);
    });

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

    // Container screenshot capture worker
    builder.Services.AddHostedService<ContainerScreenshotWorker>();

    // Image build status tracking + background worker
    builder.Services.AddScoped<ITemplateBuildService, TemplateBuildService>();
    builder.Services.AddHostedService<ImageBuildWorker>();

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
                // HTTPS metadata off in every non-production mode: dev
                // cert, docker internal http, Embedded proxy on 9100.
                options.RequireHttpsMetadata = !builder.Environment.IsLocalOrEmbedded();

                var authorityBase = authority.TrimEnd('/');
                if (builder.Environment.IsDevelopment())
                {
                    // Permissive SSL + legacy localhost:5001 issuer are
                    // Mode 1 (`dotnet run`) concessions only.
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };
                    options.TokenValidationParameters.ValidIssuers = new[]
                    {
                        authorityBase, authorityBase + "/",
                        "https://localhost:5001", "https://localhost:5001/"
                    };
                }
                else
                {
                    // Docker + Embedded: strict issuer matching against
                    // the configured AndyAuth:Authority.
                    options.TokenValidationParameters.ValidIssuers = new[]
                    {
                        authorityBase, authorityBase + "/"
                    };
                }
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

        // `dotnet run` convenience: skip RBAC for developers iterating on
        // the API. EXPLICITLY NOT applied in Embedded mode — Conductor
        // mints real JWTs and every permission check must run.
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
                Andy.Containers.Api.Services.AllowAllPolicyProvider>();
        }

        // In development, skip SSL validation for self-signed certs on RBAC API
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.ConfigureHttpClientDefaults(b =>
                b.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }));
        }
    }
    else
    {
        throw new InvalidOperationException(
            "Rbac:ApiBaseUrl is not configured. RBAC is required — set the URL in appsettings.json or appsettings.Development.json.");
    }

    // andy-settings client — fail loud if unreachable.
    //
    // andy-containers resolves provider credentials (Azure ACI registry
    // username/password, subscription/resource-group routing) and the
    // system-default LLM API keys injected into spawned containers
    // (ANTHROPIC / OPENAI / GOOGLE) from andy-settings. There is no
    // local fallback — running without andy-settings means imports and
    // Azure launches silently fail or fall back to stale env vars, which
    // is exactly the class of bug epic rivoli-ai/conductor#771 deletes.
    //
    // AddAndySettingsClient registers IAndySettingsClient (HTTP),
    // ISettingsSnapshot (hot-path cache), and SettingsRefreshService
    // (background poll of TrackedKeys every 30 s).
    var settingsBaseUrl = builder.Configuration["AndySettings:ApiBaseUrl"];
    if (string.IsNullOrWhiteSpace(settingsBaseUrl))
    {
        throw new InvalidOperationException(
            "AndySettings:ApiBaseUrl must be configured. andy-containers sources its " +
            "provider credentials + code-assistant API keys from andy-settings and " +
            "will not start without it.");
    }
    builder.Services.AddAndySettingsClient(builder.Configuration);

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

    // Messaging (ADR 0001) — registers IMessageBus (InMemory by default,
    // Nats when Messaging:Provider=Nats) and the OutboxDispatcher.
    builder.Services.AddContainersMessaging(builder.Configuration);

    var app = builder.Build();

    // Auto-migrate (PostgreSQL) or auto-create (SQLite) and seed.
    //
    // EnsureCreated only creates the DB if missing — it does NOT apply
    // migrations to an existing DB. For PostgreSQL that silently drops
    // schema changes (e.g. the `AddContainerStoryId` migration would
    // never take effect). Use Migrate there instead.
    //
    // SQLite migrations in this project use Npgsql-specific types
    // (`type: "uuid"`) and are not portable, so keep the EnsureCreated
    // shortcut for the embedded path. When the model changes, the
    // existing SQLite file must be deleted (Conductor ships it under
    // `Application Support/ai.rivoli.conductor/db/`) or patched
    // manually; there is no in-place upgrade path on SQLite today.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        if (db.Database.IsNpgsql())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();
        await DataSeeder.SeedAsync(db);
    }

    app.UseHttpsRedirection();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            // Cache JS/CSS chunks with hashed names for 1 year
            if (ctx.File.Name.EndsWith(".js") || ctx.File.Name.EndsWith(".css"))
            {
                ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            }
            // Never cache index.html — ensures fresh chunk references
            else if (ctx.File.Name == "index.html")
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
            }
        }
    });

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
    app.MapFallbackToFile("index.html");

    Log.Information("Andy Containers API starting");
    Log.Information("Swagger UI: https://localhost:5200/swagger");
    Log.Information("MCP endpoint: https://localhost:5200/mcp");
    Log.Information("Health: https://localhost:5200/health");

    app.Run();
}
catch (Exception ex)
{
    // Log + rethrow so the host propagates a non-zero exit code AND so
    // WebApplicationFactory-based tests can assert on the real exception
    // instead of swallowing it behind "entry point exited without ever
    // building an IHost". Without the rethrow, startup misconfiguration
    // (e.g. missing AndySettings:ApiBaseUrl) looks like a clean exit.
    Log.Fatal(ex, "Andy Containers API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

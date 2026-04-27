using System.Security.Claims;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
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

    // Startup-only externalId reconciler (conductor #840) — closes the
    // ~25 s cold-start window during which orphan rows show as Running
    // before the periodic ContainerStatusSyncWorker catches them.
    builder.Services.AddHostedService<ContainerExternalIdReconciler>();

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
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "AndyAuth:Authority is not configured. Authentication is required outside the " +
                "Development environment — set AndyAuth:Authority in appsettings.json or via the " +
                "AndyAuth__Authority environment variable.");
        }

        // Development only: no remote token validation. The dev-identity middleware below
        // synthesizes an admin ClaimsPrincipal when no bearer token is presented.
        builder.Services.AddAuthentication("Bearer")
            .AddJwtBearer("Bearer", options =>
            {
                options.RequireHttpsMetadata = false;
            });
        builder.Services.AddAuthorization(options =>
        {
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
                if (builder.Environment.IsDevelopment())
                {
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };
                    // Accept localhost:5001 issuer (fixed in andy-auth) even when
                    // authority is host.docker.internal:5001
                    var authorityBase = authority.TrimEnd('/');
                    options.TokenValidationParameters.ValidIssuers = new[]
                    {
                        authorityBase, authorityBase + "/",
                        "https://localhost:5001", "https://localhost:5001/"
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

        // TODO: Remove once RBAC NuGet packages are updated — bypass permission checks in dev
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

    // AP3 (rivoli-ai/andy-containers#105). Configurator pipeline:
    // andy-agents lookup (stubbed until Epic W lands) → headless-config
    // builder → on-disk writer. RunsController invokes the facade after
    // persisting a Pending Run. AP6 picks the file up to spawn andy-cli.
    builder.Services.AddSingleton<IAndyAgentsClient, StubAndyAgentsClient>();
    builder.Services.AddSingleton<IHeadlessConfigBuilder, HeadlessConfigBuilder>();
    builder.Services.AddSingleton<IHeadlessConfigWriter, HeadlessConfigWriter>();
    builder.Services.AddScoped<IRunConfigurator, RunConfigurator>();

    // AP10 (rivoli-ai/andy-containers#112). Run-scoped token issuer +
    // secrets-scope settings. Singleton issuer so the runId→token map
    // survives across configurator + runner request scopes; the
    // configurator mints, the runner revokes on terminal observation.
    // Replace StubTokenIssuer with the Y6 HTTP client when that ships.
    builder.Services.Configure<SecretsOptions>(
        builder.Configuration.GetSection(SecretsOptions.SectionName));
    builder.Services.AddSingleton<ITokenIssuer, StubTokenIssuer>();

    // AP7 (rivoli-ai/andy-containers#109). In-process registry of active
    // runs so the cancel endpoint can signal the AP6 runner across
    // request scopes. Singleton — runner registrations span requests.
    builder.Services.AddSingleton<IRunCancellationRegistry, RunCancellationRegistry>();

    // AP6 (rivoli-ai/andy-containers#108). Headless runner: spawns
    // andy-cli inside the run's container, captures exit code, publishes
    // the terminal run.* event to the outbox.
    builder.Services.AddScoped<IHeadlessRunner, HeadlessRunner>();

    // AP5 (rivoli-ai/andy-containers#107). Mode dispatcher: selects the
    // run's container, transitions Pending → Provisioning, and routes
    // headless runs to the runner above (terminal/desktop modes branch
    // independently). RunsController hands off to it after configurator
    // success.
    builder.Services.AddScoped<IRunModeDispatcher, RunModeDispatcher>();

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
    Log.Fatal(ex, "Andy Containers API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

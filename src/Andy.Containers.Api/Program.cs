using System.Security.Claims;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using Andy.Containers.DependencyInjection;
using Andy.Containers.Infrastructure.Audit;
using Andy.Containers.Infrastructure.Build.Local;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Infrastructure.Registries.Local;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Andy.Containers.Api.Telemetry;
using Andy.Rbac.Client;
using Andy.Telemetry;
using OpenTelemetry.Trace;
using Serilog;
using System.Text.Json.Serialization;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// RC3 (#201). `dotnet Andy.Containers.Api migrate` short-circuits
// the host build and runs EF migrations only — the path Helm's
// pre-install / pre-upgrade Job (RC6) takes so the rollout is
// decoupled from per-pod startup migration races. Default behaviour
// (no args) is unchanged: the API boots and applies migrations
// in-process unless `Database:MigrateOnStartup` is `false`.
if (args.Length > 0 && args[0] == "migrate")
{
    try
    {
        return await Andy.Containers.Api.MigrationEntryPoint.RunAsync(args[1..]);
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

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

    // #944 / M1.5.1 foundation. M2M token consumer that mints
    // service-to-service tokens from andy-auth via the
    // `client_credentials` grant. Used by future container-side
    // env-var injection (the next slice of #944) so a code assistant
    // installed inside a container can authenticate against
    // andy-models without the user supplying a token.
    builder.Services.Configure<ServiceAuthOptions>(
        builder.Configuration.GetSection(ServiceAuthOptions.SectionName));
    builder.Services.AddHttpClient(ServiceTokenService.HttpClientName);
    builder.Services.AddSingleton<IServiceTokenService, ServiceTokenService>();

    // rivoli-ai/conductor#943 (M1.5.1). Per-container proxy-token
    // consumer. Talks to andy-models'
    // `POST /api/proxy/tokens` (M1.3.3) using the M2M bearer from
    // IServiceTokenService and returns a JWT scoped to the container's
    // requested model slugs. ContainerOrchestrationService injects that
    // JWT into the container as ANDY_SERVICE_TOKEN — narrower than the
    // M2M bearer the orchestrator carries itself.
    builder.Services.Configure<AndyModelsOptions>(
        builder.Configuration.GetSection(AndyModelsOptions.SectionName));
    builder.Services.AddHttpClient(AndyModelsProxyTokenService.HttpClientName);
    builder.Services.AddSingleton<IProxyTokenService, AndyModelsProxyTokenService>();

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

    // #277 PR C. Reclaim abandoned template-upload staging dirs.
    // Periodic sweep over <temp>/andy-containers/template-uploads/staging/
    // that deletes <stagingId> subdirs no longer referenced by any
    // Template.UploadedFilesPath row and older than the configured
    // retention (default 7 days).
    builder.Services.Configure<TemplateUploadStagingCleanupOptions>(
        builder.Configuration.GetSection(TemplateUploadStagingCleanupOptions.SectionName));
    builder.Services.AddHostedService<TemplateUploadStagingCleanupWorker>();

    // Git credential + clone services
    //
    // RC2 (#200). Persist the Data Protection key ring in the DB
    // (`DataProtectionKeys` table) rather than the previous on-disk
    // volume mount. Two API replicas behind a Service must decrypt
    // each other's cookies / anti-forgery tokens; an RWO PVC can't
    // guarantee that, so the DB row store is the multi-replica path.
    // SetApplicationName isolates this app's keys from any other DP
    // user on the same DB (defensive — there isn't one today).
    builder.Services.AddDataProtection()
        .SetApplicationName("andy-containers")
        .PersistKeysToDbContext<ContainersDbContext>();
    builder.Services.AddScoped<IGitCredentialService, GitCredentialService>();
    builder.Services.AddScoped<IGitCloneService, GitCloneService>();
    builder.Services.AddScoped<IGitRepositoryProbeService, GitRepositoryProbeService>();
    // F6.1 (rivoli-ai/conductor#1940): per-run branch + git-diff endpoint.
    builder.Services.AddScoped<IRunBranchService, RunBranchService>();
    builder.Services.AddScoped<IGitDiffService, GitDiffService>();
    // F6.4 (rivoli-ai/conductor#1943): web-port discovery + expose endpoint.
    builder.Services.AddScoped<IPortDiscoveryService, PortDiscoveryService>();
    builder.Services.AddSingleton<ICodeAssistantInstallService, CodeAssistantInstallService>();
    // rivoli-ai/conductor#945 (M1.5.3). Scoped so it pulls a fresh
    // IContainerService per call (which is itself scoped because it
    // takes ContainersDbContext).
    builder.Services.AddScoped<ICodeAssistantInstallExecutor, CodeAssistantInstallExecutor>();
    // IApiKeyService / IApiKeyValidationService retired in
    // rivoli-ai/conductor#946 (M1.5.4) — provider keys now live in
    // andy-settings and reach containers via the andy-models proxy.
    builder.Services.AddScoped<IToolVersionDetector, ToolVersionDetector>();
    builder.Services.AddScoped<IImageManifestService, ImageManifestService>();
    builder.Services.AddScoped<IImageDiffService, ImageDiffService>();

    // IM8 (rivoli-ai/andy-containers#262). First time the IM6 +
    // IM7 wiring composes into the API host. AddImageManagement
    // wires the abstraction layer (IRegistryConfiguration);
    // AddLocalZotRegistry registers the local-zot adapter +
    // DockerCliUploader; AddLocalBuildBackend registers the
    // engine detector + LocalBuildBackend; the orchestrator
    // ties the lot together for ImagesController.Build.
    builder.Services.AddImageManagement(builder.Configuration);
    builder.Services.AddLocalZotRegistry();
    builder.Services.AddLocalBuildBackend();
    builder.Services.AddScoped<Andy.Containers.Storage.IImageBuildOrchestrator, Andy.Containers.Infrastructure.Build.ImageBuildOrchestrator>();
    builder.Services.AddScoped<Andy.Containers.Storage.IBuildArtifactStore, Andy.Containers.Infrastructure.Data.BuildArtifactStore>();

    // rivoli-ai/conductor#1014 (M1.9.6). ensure-pull endpoint —
    // Docker CLI based image rehoster from upstream registries
    // (e.g. ghcr.io/rivoli-ai) into the local zot. Singleton
    // because it holds no per-request state and the underlying
    // CLI process is idempotent.
    builder.Services.AddSingleton<Andy.Containers.Abstractions.Images.IImagePullService,
        Andy.Containers.Infrastructure.Images.DockerCliImagePullService>();

    // IM9 (rivoli-ai/andy-containers#263). Build event bus +
    // execution registry are singletons (process-local in-memory
    // state); the async executor is a singleton too because it
    // captures IServiceScopeFactory to spawn its own scope per
    // background build.
    builder.Services.AddSingleton<Andy.Containers.Storage.IBuildEventBus, Andy.Containers.Infrastructure.Build.Events.InMemoryBuildEventBus>();
    builder.Services.AddSingleton<Andy.Containers.Storage.IBuildExecutionRegistry, Andy.Containers.Infrastructure.Build.Events.InMemoryBuildExecutionRegistry>();
    builder.Services.AddSingleton<Andy.Containers.Storage.IAsyncBuildExecutor, Andy.Containers.Infrastructure.Build.Events.AsyncBuildExecutor>();

    // F4.1 (rivoli-ai/conductor#1934). Mid-run agent output bus. Singleton —
    // process-local in-memory per-run ring buffers, shared across the AP6
    // runner (publisher) and the run-output / container-logs SSE endpoints
    // (subscribers), which live in different request scopes.
    builder.Services.AddSingleton<Andy.Containers.Storage.IRunOutputBus, Andy.Containers.Infrastructure.Runs.Events.InMemoryRunOutputBus>();

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

    // --- OpenTelemetry (via Andy.Telemetry) ---
    // OT5 (rivoli-ai/conductor#1263). Replaces the per-service OpenTelemetry
    // hand-roll with the shared library so every Andy service shares the same
    // attribute set, propagator stack, and OTLP export config. UnifiedProxy
    // already emits server-side request spans, so AspNetCore instrumentation
    // stays off here to avoid double-counting.
    builder.Services.AddAndyTelemetry(builder.Configuration, o =>
    {
        if (string.IsNullOrWhiteSpace(o.ServiceName))
            o.ServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "andy-containers";
        if (string.IsNullOrWhiteSpace(o.OtlpEndpoint))
            o.OtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(o.Protocol) || o.Protocol == "grpc")
        {
            var envProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
            if (!string.IsNullOrWhiteSpace(envProtocol))
                o.Protocol = envProtocol;
        }
        foreach (var source in Andy.Containers.Api.Telemetry.ActivitySources.All)
            o.ActivitySources.Add(source);
        foreach (var meter in Andy.Containers.Api.Telemetry.Meters.All)
            o.Meters.Add(meter);
        o.EnableAspNetCoreInstrumentation = false;
        o.EnableHttpClientInstrumentation = true;
    });
    // gRPC client + EF Core tracing are service-specific (not bundled in Andy.Telemetry).
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t
            .AddGrpcClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation());

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

    // X9 (rivoli-ai/andy-containers#99). Stub allowlist resolver until
    // andy-agents (Epic W3) ships GET /api/agents/{id}/allowed-environments.
    // The stub returns null (= no policy) so workspace-create stays open
    // for agents that haven't declared an allowlist; explicit policies
    // get enforced by the controller once the real client lands here.
    builder.Services.AddSingleton<IAgentCapabilityService, StubAgentCapabilityService>();

    // AP7 (rivoli-ai/andy-containers#109). In-process registry of active
    // runs so the cancel endpoint can signal the AP6 runner across
    // request scopes. Singleton — runner registrations span requests.
    builder.Services.AddSingleton<IRunCancellationRegistry, RunCancellationRegistry>();

    // AP6 (rivoli-ai/andy-containers#108). Headless runner: spawns
    // andy-cli inside the run's container, captures exit code, publishes
    // the terminal run.* event to the outbox.
    builder.Services.AddScoped<IHeadlessRunner, HeadlessRunner>();

    // rivoli-ai/andy-containers#320. andy-docs HTTP client for the
    // output-artifact collector. Registered only when AndyDocs:ApiBaseUrl
    // is set so dev / embedded mode (no andy-docs instance) does NOT
    // fail at startup; in that mode the collector falls back to
    // metadata-only artifacts (DocsRef stays null on every emitted
    // RunOutputArtifact, matching the pre-#320 wire shape exactly).
    builder.Services.Configure<AndyDocsOptions>(
        builder.Configuration.GetSection(AndyDocsOptions.SectionName));
    var docsBaseUrl = builder.Configuration[$"{AndyDocsOptions.SectionName}:ApiBaseUrl"];
    if (!string.IsNullOrWhiteSpace(docsBaseUrl))
    {
        var docsOptions = builder.Configuration
            .GetSection(AndyDocsOptions.SectionName)
            .Get<AndyDocsOptions>() ?? new AndyDocsOptions();
        var docsBuilder = builder.Services.AddHttpClient(AndyDocsHttpClient.HttpClientName, client =>
        {
            // Trailing slash so URI resolution preserves any path
            // prefix (e.g. embedded mode where the base might be
            // http://localhost:9100/docs).
            client.BaseAddress = new Uri(docsBaseUrl.TrimEnd('/') + "/");
            client.Timeout = docsOptions.Timeout;
        });
        // Attach the M2M bearer when AndyAuth is configured. In bypass
        // mode (Authority empty) we leave the client anonymous, mirroring
        // the existing AndyModels / inbound JWT-bearer postures.
        var docsAuthority = builder.Configuration["AndyAuth:Authority"];
        if (!string.IsNullOrWhiteSpace(docsAuthority))
        {
            var audience = docsOptions.Audience;
            docsBuilder.AddHttpMessageHandler(sp =>
            {
                var tokens = sp.GetRequiredService<IServiceTokenService>();
                var logger = sp.GetService<ILogger<ServiceBearerHandler>>();
                return new ServiceBearerHandler(
                    async ct =>
                    {
                        try
                        {
                            return await tokens.GetAccessTokenAsync(audience, ct);
                        }
                        catch (ServiceTokenException)
                        {
                            // Surface as "no token" — the handler then
                            // sends anonymously and andy-docs replies
                            // 401, which the client treats as a normal
                            // upload failure (best-effort fallback).
                            return null;
                        }
                    },
                    logger);
            });
        }
        builder.Services.AddSingleton<IAndyDocsClient, AndyDocsHttpClient>();
    }

    // rivoli-ai/andy-containers#316. Output-artifact collector:
    // probes /workspace/.andy/outputs/ via IContainerService.ExecAsync
    // at terminal-event time and emits the manifest on the run.* event
    // payload (and persists onto Run.OutputArtifacts for the agent-run
    // path). Scoped because the default impl pulls in the request-scoped
    // IContainerService and a request-scoped logger.
    //
    // #320: the collector now also pushes each artifact's bytes to
    // andy-docs via the optionally-registered IAndyDocsClient above.
    // The dependency is optional — when the client isn't registered
    // (no AndyDocs:ApiBaseUrl) the collector emits metadata-only.
    builder.Services.AddScoped<IOutputArtifactCollector, FilesystemOutputArtifactCollector>();

    // EX.7 (rivoli-ai/andy-containers#328). Input-artifact stager: the
    // inverse of the collector. Before andy-cli spawns, it downloads each
    // declared input's andy-docs document and writes it under
    // /workspace/.andy/inputs/ inside the container. Scoped for the same
    // reason as the collector (request-scoped IContainerService + logger).
    // The optional IAndyDocsClient is shared with the collector; when no
    // andy-docs is configured a run that declares inputs fails the run
    // start (staging is impossible) — but a run with no inputs is
    // unaffected.
    builder.Services.AddScoped<IInputArtifactStager, FilesystemInputArtifactStager>();

    // AP5 (rivoli-ai/andy-containers#107). Mode dispatcher: selects the
    // run's container, transitions Pending → Provisioning, and routes
    // headless runs to the runner above (terminal/desktop modes branch
    // independently). RunsController hands off to it after configurator
    // success.
    builder.Services.AddScoped<IRunModeDispatcher, RunModeDispatcher>();

    var app = builder.Build();

    // Auto-migrate on both providers. Conductor #883.
    //
    // EnsureCreated only creates the DB if missing — it does NOT apply
    // migrations to an existing DB. For PostgreSQL that silently drops
    // schema changes (e.g. the `AddContainerStoryId` migration would
    // never take effect). For SQLite it had the same effect: every
    // model change shipped a footgun that 500-stormed the Containers
    // tab on existing users until someone hand-rolled an `ALTER TABLE`.
    //
    // The Npgsql column types in our existing migrations (`uuid`,
    // `jsonb`, `timestamp with time zone`) translate cleanly under
    // EF Core's SQLite provider — verified by
    // `SqliteAutoMigrationProbeTests.MigrateAsync_AppliesAllMigrationsToFreshDb`.
    // The only stumbling block is existing users whose DB was created
    // by the previous `EnsureCreatedAsync` branch and therefore has
    // schema but no `__EFMigrationsHistory`; `SqliteMigrationBootstrap`
    // detects that case and seeds the history before letting
    // `MigrateAsync` run.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

        // RC3 (#201). `Database:MigrateOnStartup` defaults to true so
        // existing single-process compose deploys are unchanged. Helm
        // (RC6) sets it false and runs the dedicated `migrate` Job
        // before the rollout — avoiding the multi-replica race where
        // every pod tries to apply schema changes on startup.
        var migrateOnStartup = builder.Configuration
            .GetValue<bool?>("Database:MigrateOnStartup") ?? true;
        if (migrateOnStartup)
        {
            var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            await Andy.Containers.Api.MigrationEntryPoint
                .ApplyMigrationsAsync(db, loggerFactory);
        }
        await DataSeeder.SeedAsync(db);

        // X2 (rivoli-ai/andy-containers#91). Load the EnvironmentProfile
        // catalog from config/environments/global/*.yaml. Idempotent —
        // existing rows are left alone so operator hand-edits via the
        // X3 catalog API are preserved across restarts.
        var seederLogger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EnvironmentProfileSeeder));
        await EnvironmentProfileSeeder.SeedAsync(db, app.Environment, seederLogger);

        // Conductor #886. Theme catalog from config/themes/global/*.yaml.
        // Unlike EnvironmentProfile, themes are read-only (no POST API
        // in v1) so re-seeding always reconciles existing rows to
        // whatever the YAML says.
        var themeSeederLogger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ThemeSeeder));
        await ThemeSeeder.SeedAsync(db, app.Environment, themeSeederLogger);
    }

    app.UseHttpsRedirection();

    // HC.8.1 of rivoli-ai/conductor#1245: expose the OpenAPI
    // document in every environment so Conductor's in-app Help Center
    // can ingest /openapi.json from the bundled service. The Swagger
    // UI itself stays development-only.
    app.UseSwagger();
    if (app.Environment.IsDevelopment())
    {
        app.UseSwaggerUI();
    }
    // Stable alias so every andy-* service exposes the same
    // path. HC.8.1 of rivoli-ai/conductor#1245.
    app.MapGet("/openapi.json", () => Results.Redirect("/swagger/v1/swagger.json"))
        .ExcludeFromDescription();

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

    // OT5 (rivoli-ai/conductor#1263): Prometheus /metrics endpoint
    // exposed by Andy.Telemetry. OTLP push is independent.
    app.MapAndyTelemetry();

    app.MapFallbackToFile("index.html");

    Log.Information("Andy Containers API starting");
    Log.Information("Swagger UI: https://localhost:5200/swagger");
    Log.Information("MCP endpoint: https://localhost:5200/mcp");
    Log.Information("Health: https://localhost:5200/health");

    app.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Andy Containers API terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

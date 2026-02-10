using Andy.Containers.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Register the API HttpClient for ContainersApiService
builder.Services.AddHttpClient<ContainersApiService>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration.GetValue<string>("ApiBaseUrl") ?? "https://localhost:5200");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Configure authentication with OpenID Connect (same pattern as Andy RBAC)
var andyAuthAuthority = builder.Configuration["AndyAuth:Authority"] ?? "https://localhost:5001";
var clientId = builder.Configuration["AndyAuth:ClientId"] ?? "andy-containers-web";
var clientSecret = builder.Configuration["AndyAuth:ClientSecret"];

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.Name = "AndyContainers.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
})
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.Authority = andyAuthAuthority;
    options.ClientId = clientId;
    options.ClientSecret = clientSecret;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SaveTokens = true;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");

    options.ClaimActions.MapJsonKey("email", "email");
    options.ClaimActions.MapJsonKey("name", "name");
    options.ClaimActions.MapJsonKey("picture", "picture");

    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        NameClaimType = "name",
        RoleClaimType = "role"
    };

    options.Events = new OpenIdConnectEvents
    {
        OnRemoteFailure = context =>
        {
            context.Response.Redirect("/");
            context.HandleResponse();
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider,
    Microsoft.AspNetCore.Components.Server.ServerAuthenticationStateProvider>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Authentication endpoints
app.MapGet("authentication/login", async (HttpContext context, string? returnUrl) =>
{
    returnUrl ??= "/";
    await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = returnUrl
    });
}).AllowAnonymous();

app.MapPost("authentication/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = "/"
    });
}).AllowAnonymous();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host").RequireAuthorization();

app.Run();

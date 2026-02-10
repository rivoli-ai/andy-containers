using Andy.Containers.Web.Services;

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

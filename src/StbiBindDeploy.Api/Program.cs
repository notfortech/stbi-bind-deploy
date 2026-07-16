using StbiBindDeploy.Api.Options;
using StbiBindDeploy.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Port binding for Azure App Service Linux ─────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ── Flat env-var -> nested config path mapping ───────────────────────────
// Azure App Service Application Settings can't use colon syntax; map them here.
// PowerBI:* intentionally reuses the same env var names as koru-main's own PowerBI settings
// (same app registration, same credentials) so the two services can be configured from the
// same values without renaming anything.
var envOverrides = new Dictionary<string, string?>
{
    ["PowerBI:TenantId"] = Environment.GetEnvironmentVariable("POWERBI_TENANT_ID"),
    ["PowerBI:ClientId"] = Environment.GetEnvironmentVariable("POWERBI_CLIENT_ID"),
    ["PowerBI:ClientSecret"] = Environment.GetEnvironmentVariable("POWERBI_CLIENT_SECRET"),
    ["PowerBI:CapacityId"] = Environment.GetEnvironmentVariable("POWERBI_CAPACITY_ID"),
    // Shared secret this service expects as the X-Service-Api-Key header on every request.
    // Whichever service calls this one (koru-main, once S8 wires up a real caller) must send
    // this same value.
    ["Security:KoruApiKey"] = Environment.GetEnvironmentVariable("INBOUND_SERVICE_API_KEY"),
};
builder.Configuration.AddInMemoryCollection(envOverrides.Where(kv => kv.Value is not null)!);

// ── Options ───────────────────────────────────────────────────────────────
builder.Services.AddOptions<PowerBiOptions>().BindConfiguration(PowerBiOptions.SectionName);
builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName);

// ── Power BI client ───────────────────────────────────────────────────────
builder.Services.AddHttpClient<IPowerBiClient, PowerBiClient>();

// ── API + health checks ──────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "stbi-bind-deploy", status = "ok" }));

app.Run();

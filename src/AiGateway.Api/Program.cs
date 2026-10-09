using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Amazon.S3;
using AiGateway.Api;
using AiGateway.Core;
using AiGateway.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Container healthcheck without curl in the runtime image.
if (args.Contains("--healthcheck"))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { return (await probe.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var env = builder.Environment;
var devLike = env.IsDevelopment() || env.IsEnvironment("Testing");
var demo = cfg.GetValue<bool>("Demo:Enabled");

// Fail closed: production never runs the demo provider or an unprotected keyring.
if (!devLike && demo) throw new InvalidOperationException("Demo:Enabled must be false outside Development/Testing.");
if (!devLike && string.IsNullOrEmpty(cfg["DataProtection:CertificatePath"]))
    throw new InvalidOperationException("Production requires DataProtection:CertificatePath to protect the keyring.");

builder.Services.AddDbContext<GatewayDb>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // rejects tools/audio/unknown fields
    o.SerializerOptions.Encoder = JsonDefaults.Options.Encoder;
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = cfg.GetValue("Limits:UploadMaxBytes", 10L << 20) + 65536);

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequiredLength = 10;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<GatewayDb>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies(c => c.ApplicationCookie!.Configure(o =>
{
    o.Cookie.Name = "aigw";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    // Local HTTP dev only; documented in SECURITY.md.
    o.Cookie.SecurePolicy = devLike ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
}));
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "aigw-csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = devLike ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("RateLimit:AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = (ctx, _) => Errors.Write(ctx.HttpContext, new GatewayException(429, "rate_limit_exceeded", "Çok fazla deneme.", true, TimeSpan.FromMinutes(1)));
});

var dp = builder.Services.AddDataProtection().SetApplicationName("AiGateway");
if (cfg["DataProtection:KeyRingPath"] is { Length: > 0 } ring) dp.PersistKeysToFileSystem(new DirectoryInfo(ring));
if (cfg["DataProtection:CertificatePath"] is { Length: > 0 } cert)
    dp.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(cert, cfg["DataProtection:CertificatePassword"]));
builder.Services.AddSingleton<SecretProtector>();

if (!string.IsNullOrEmpty(cfg["Storage:Endpoint"]))
    builder.Services.AddSingleton<IObjectStorage>(_ => new S3Storage(new AmazonS3Client(cfg["Storage:AccessKey"], cfg["Storage:SecretKey"],
        new AmazonS3Config { ServiceURL = cfg["Storage:Endpoint"], ForcePathStyle = true, AuthenticationRegion = "us-east-1" }), cfg["Storage:Bucket"]!));
else if (devLike) builder.Services.AddSingleton<IObjectStorage, MemoryStorage>();
else throw new InvalidOperationException("Storage:Endpoint is required.");

// Fixed official hosts only; no endpoint customization (SSRF). SDK-style retries are not added.
foreach (var (name, url) in new[] { ("openai", OpenAiAdapter.BaseUrl), ("anthropic", AnthropicAdapter.BaseUrl) })
    builder.Services.AddHttpClient(name, c => { c.BaseAddress = new Uri(url); c.Timeout = Timeout.InfiniteTimeSpan; })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), AllowAutoRedirect = false });
string[] providers = demo ? ["openai", "anthropic", "demo"] : ["openai", "anthropic"];
builder.Services.AddScoped(sp => new ProviderRegistry(sp, providers));
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<InferenceService>();
if (!env.IsEnvironment("Testing")) builder.Services.AddHostedService<Reconciler>();

builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowCredentials().AllowAnyHeader().WithMethods("GET", "POST", "PATCH", "PUT", "DELETE")));
var otel = builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("aigateway"))
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter(InferenceService.Meter.Name))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
if (!string.IsNullOrEmpty(cfg["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();

var app = builder.Build();

if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<GatewayDb>().Database.MigrateAsync();
    Console.WriteLine("Migrations applied.");
    return 0;
}

app.Use(async (ctx, next) =>
{
    ctx.Items["requestId"] = Guid.CreateVersion7();
    ctx.Response.Headers["X-Request-Id"] = ctx.Items["requestId"]!.ToString();
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    try { await next(); }
    catch (GatewayException e) when (!ctx.Response.HasStarted) { await Errors.Write(ctx, e); }
    catch (BadHttpRequestException e) when (!ctx.Response.HasStarted)
    {
        await Errors.Write(ctx, e.StatusCode == 413
            ? new GatewayException(413, "upload_too_large", "İstek gövdesi çok büyük.")
            : GatewayException.Invalid("İstek gövdesi geçersiz veya desteklenmeyen alan içeriyor."));
    }
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (GatewayDb db, IObjectStorage storage, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) && await storage.ReadyAsync(ct)
        ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "unavailable" }, statusCode: 503));
app.MapGet("/api/v1/meta", () => new { demo, environment = env.EnvironmentName, providers });
if (devLike) app.MapOpenApi("/openapi/v1.json");

AdminEndpoints.Map(app);
InferenceEndpoints.Map(app);

app.Run();
return 0;

public partial class Program;

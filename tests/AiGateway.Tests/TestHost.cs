using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AiGateway.Tests;

/// <summary>Scripted provider HTTP endpoint; no test ever reaches a real provider or spends money.</summary>
public class FakeProvider : HttpMessageHandler
{
    public List<(string Url, string Body)> Calls { get; } = [];
    public Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add((request.RequestUri!.ToString(), body));
        return await Respond(request, body, ct);
    }

    public static HttpResponseMessage Json(object body, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(Stream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } } };
}

public class LogCapture(System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILoggerProvider, ILogger
{
    public ILogger CreateLogger(string category) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> fmt) => sink.Enqueue(fmt(state, e) + e);
    public void Dispose() { }
}

public class GatewayFactory : WebApplicationFactory<Program>
{
    public FakeProvider Fake { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string> Logs { get; } = new();
    public string Db { get; } = $"aigw_test_{Guid.NewGuid():N}";
    static string PgServer => Environment.GetEnvironmentVariable("TEST_POSTGRES") ?? "Host=localhost;Port=55432;Username=postgres;Password=postgres";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"{PgServer};Database={Db}");
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("RateLimit:AuthPerMinute", "100000"); // TestServer has no client IP; throttling has its own test
        builder.UseSetting("DataProtection:KeyRingPath", Path.Combine(Path.GetTempPath(), Db));
        builder.ConfigureServices(s =>
        {
            s.AddLogging(l => l.AddProvider(new LogCapture(Logs)).SetMinimumLevel(LogLevel.Trace));
            foreach (var name in new[] { "openai", "anthropic" })
                s.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Fake);
        });
    }

    public async Task InitAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GatewayDb>().Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        using (var scope = Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<GatewayDb>().Database.EnsureDeletedAsync();
        await base.DisposeAsync();
    }

    public T With<T>(Func<IServiceProvider, T> f)
    {
        var scope = Services.CreateScope();
        return f(scope.ServiceProvider);
    }
}

/// <summary>Cookie session + CSRF header, like the web panel.</summary>
public class Admin(HttpClient http)
{
    public HttpClient Http => http;
    string? _csrf;

    public async Task RefreshCsrf() =>
        _csrf = (await http.GetFromJsonAsync<JsonObject>("/api/v1/auth/csrf"))!["token"]!.GetValue<string>();

    public async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, bool csrf = true)
    {
        if (_csrf is null) await RefreshCsrf();
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (csrf) req.Headers.Add("X-CSRF-TOKEN", _csrf);
        return await http.SendAsync(req);
    }

    public async Task<JsonNode> Ok(HttpMethod method, string url, object? body = null)
    {
        var res = await Send(method, url, body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{method} {url} → {(int)res.StatusCode}: {text}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    public Task<JsonNode> Get(string url) => Ok(HttpMethod.Get, url);
    public Task<JsonNode> Post(string url, object? body = null) => Ok(HttpMethod.Post, url, body);
}

public record Tenant(Admin Admin, Guid Ws, Guid Project, Guid DevEnv, Guid ProdEnv, Guid ProfileId, string Key, HttpClient Api)
{
    public string W => $"/api/v1/workspaces/{Ws}";
}

public static class Setup
{
    public static async Task<Admin> Register(GatewayFactory f, string? email = null)
    {
        var admin = new Admin(f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await admin.Post("/api/v1/auth/register", new { email = email ?? $"{Guid.NewGuid():N}@example.test", password = "Correct-horse-9", workspaceName = "Test" });
        await admin.RefreshCsrf(); // antiforgery tokens are bound to the signed-in user
        return admin;
    }

    /// <summary>Workspace with a project, a published profile "p" in the dev environment and a project API key.</summary>
    public static async Task<Tenant> Tenant(GatewayFactory f, object? config = null)
    {
        var admin = await Register(f);
        var ws = Guid.Parse((await admin.Get("/api/v1/auth/me"))["workspaces"]![0]!["id"]!.GetValue<string>());
        var w = $"/api/v1/workspaces/{ws}";
        var project = Guid.Parse((await admin.Post($"{w}/projects", new { name = "App" }))["id"]!.GetValue<string>());
        var envs = (await admin.Get($"{w}/projects/{project}/environments")).AsArray();
        var dev = Guid.Parse(envs.First(e => e!["type"]!.GetValue<string>() == "development")!["id"]!.GetValue<string>());
        var prod = Guid.Parse(envs.First(e => e!["type"]!.GetValue<string>() == "production")!["id"]!.GetValue<string>());
        if (config is null) config = Config("demo", "demo-model", null);
        else if (config is Func<Admin, string, Task<object>> make) config = await make(admin, w);
        var profile = Guid.Parse((await admin.Post($"{w}/profiles", new { name = "p" }))["id"]!.GetValue<string>());
        var rev = (await admin.Post($"{w}/profiles/{profile}/revisions", new { config }))["id"]!.GetValue<string>();
        await admin.Ok(HttpMethod.Put, $"{w}/environments/{dev}/profiles/{profile}", new { revisionId = rev });
        var key = (await admin.Post($"{w}/environments/{dev}/keys", new { name = "k" }))["key"]!.GetValue<string>();
        var api = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return new Tenant(admin, ws, project, dev, prod, profile, key, api);
    }

    public static object Config(string provider, string model, Guid? connectionId, int retries = 0, object[]? fallbacks = null, int timeout = 30) => new
    {
        requiredCapabilities = new[] { "text" },
        primary = new { provider, model, connectionId },
        fallbacks = fallbacks ?? [],
        outputTokenLimit = 500,
        timeoutSeconds = timeout,
        schemaMode = "native",
        pricingRequiredForBudgetEnforcement = true,
        automaticRetryCount = retries,
    };

    /// <summary>Config factory that first creates a provider connection in the tenant.</summary>
    public static Func<Admin, string, Task<object>> WithConnection(string provider, string model, int retries = 0, Func<Guid, object[]>? fallbacks = null, int timeout = 30) =>
        async (admin, w) =>
        {
            var conn = Guid.Parse((await admin.Post($"{w}/provider-connections", new { provider, name = provider, apiKey = "test-key-not-real-0000000000" }))["id"]!.GetValue<string>());
            return Config(provider, model, conn, retries, fallbacks?.Invoke(conn), timeout);
        };

    public static byte[] Png(int w = 8, int h = 8)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    public static async Task<HttpResponseMessage> Upload(HttpClient api, byte[] bytes, string url = "/api/v1/uploads", string name = "a.png")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
        return await api.PostAsync(url, form);
    }

    public static object TextBody(string text = "Merhaba", bool stream = false, string profile = "p", string? endUser = null) => new
    {
        profile,
        messages = new[] { new { role = "user", content = new object[] { new { type = "text", text } } } },
        stream,
        endUserRef = endUser,
    };

    public static async Task<(int Status, JsonNode? Body)> Generate(HttpClient api, object body, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/generations") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        var res = await api.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, text.Length > 0 && !text.StartsWith("event") ? JsonNode.Parse(text) : JsonValue.Create(text));
    }
}

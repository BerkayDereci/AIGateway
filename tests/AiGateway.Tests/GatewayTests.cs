using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AiGateway.Core;
using AiGateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiGateway.Tests;

public class Fixture : IAsyncLifetime
{
    public GatewayFactory F { get; } = new();
    public Task InitializeAsync() => F.InitAsync();
    public async Task DisposeAsync() => await F.DisposeAsync();
}

public class GatewayTests(Fixture fx) : IClassFixture<Fixture>
{
    readonly GatewayFactory f = fx.F;

    static string Code(JsonNode? n) => (string)n!["code"]!;

    T Db<T>(Func<GatewayDb, T> q) => f.With(sp => q(sp.GetRequiredService<GatewayDb>()));

    // ---------- Tenant isolation & roles ----------

    [Fact]
    public async Task Workspace_B_cannot_read_or_modify_A()
    {
        var a = await Setup.Tenant(f);
        var b = await Setup.Tenant(f);
        var upload = await (await Setup.Upload(a.Api, Setup.Png())).Content.ReadFromJsonAsync<JsonObject>();
        await Setup.Generate(a.Api, Setup.TextBody());
        var requestId = Db(db => db.Requests.First(r => r.WorkspaceId == a.Ws).Id);

        foreach (var url in new[] { $"{a.W}/projects/{a.Project}", $"{a.W}/projects", $"{a.W}/profiles", $"{a.W}/usage",
                     $"{a.W}/environments/{a.DevEnv}/keys", $"{a.W}/requests/{requestId}", $"{a.W}/provider-connections", $"{a.W}/limits" })
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.Send(HttpMethod.Get, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.Send(HttpMethod.Patch, $"{a.W}/projects/{a.Project}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.Send(HttpMethod.Post, $"{a.W}/environments/{a.DevEnv}/keys", new { name = "x" })).StatusCode);
        // B's own workspace cannot reference A's environment either.
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.Send(HttpMethod.Post, $"{b.W}/environments/{a.DevEnv}/keys", new { name = "x" })).StatusCode);

        // API key routes: B's key cannot use or delete A's asset.
        var body = new { profile = "p", messages = new[] { new { role = "user", content = new object[] { new { type = "image", assetId = (string)upload!["assetId"]! } } } } };
        var (status, err) = await Setup.Generate(b.Api, body);
        Assert.Equal((400, "invalid_request"), (status, Code(err)));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Api.DeleteAsync($"/api/v1/uploads/{upload["assetId"]}")).StatusCode);
        Assert.Equal("available", Db(db => db.Uploads.Single(u => u.Id == Guid.Parse((string)upload["assetId"]!)).State));
    }

    [Fact]
    public async Task Database_rejects_cross_workspace_links()
    {
        var a = await Setup.Tenant(f);
        var b = await Setup.Tenant(f);
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => f.With(async sp =>
        {
            var db = sp.GetRequiredService<GatewayDb>();
            db.ApiKeys.Add(new ApiKey { WorkspaceId = b.Ws, EnvironmentId = a.DevEnv, Name = "x", Prefix = Guid.NewGuid().ToString("N")[..16], KeyHash = [1] });
            await db.SaveChangesAsync();
            return 0;
        }));
        Assert.Contains("foreign key", ex.InnerException!.Message);
    }

    [Fact]
    public async Task Viewer_cannot_create_keys_verify_or_run_inference()
    {
        var owner = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        var email = $"{Guid.NewGuid():N}@example.test";
        var viewer = await Setup.Register(f, email);
        await owner.Admin.Post($"{owner.W}/members", new { email, role = "viewer" });
        var conn = (string)(await owner.Admin.Get($"{owner.W}/provider-connections"))[0]!["id"]!;

        Assert.Equal(HttpStatusCode.OK, (await viewer.Send(HttpMethod.Get, $"{owner.W}/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Send(HttpMethod.Post, $"{owner.W}/environments/{owner.DevEnv}/keys", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Send(HttpMethod.Post, $"{owner.W}/provider-connections/{conn}/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Send(HttpMethod.Post, $"{owner.W}/environments/{owner.DevEnv}/generations", Setup.TextBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Send(HttpMethod.Get, $"{owner.W}/audit")).StatusCode);
        Assert.DoesNotContain(f.Fake.Calls, c => c.Url.EndsWith("models"));
    }

    [Fact]
    public async Task Revoked_and_expired_keys_get_401_and_keys_cannot_reach_admin_api()
    {
        var t = await Setup.Tenant(f);
        var keyId = (string)(await t.Admin.Get($"{t.W}/environments/{t.DevEnv}/keys"))[0]!["id"]!;
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody())).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Api.GetAsync($"{t.W}/projects")).StatusCode);

        await t.Admin.Ok(HttpMethod.Delete, $"{t.W}/environments/{t.DevEnv}/keys/{keyId}");
        var (status, body) = await Setup.Generate(t.Api, Setup.TextBody());
        Assert.Equal((401, "invalid_key"), (status, Code(body)));

        var created = await t.Admin.Post($"{t.W}/environments/{t.DevEnv}/keys", new { name = "short", expiresAt = DateTime.UtcNow.AddSeconds(2) });
        var api = f.CreateClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", (string)created["key"]!);
        await Task.Delay(2500);
        Assert.Equal(401, (await Setup.Generate(api, Setup.TextBody())).Status);
    }

    [Fact]
    public async Task Secrets_are_shown_once_and_stored_protected()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("anthropic", "claude-haiku-5-5"));
        var list = (await t.Admin.Get($"{t.W}/environments/{t.DevEnv}/keys")).ToJsonString();
        Assert.DoesNotContain(t.Key, list);
        Assert.Contains(t.Key[..16], list);
        var conns = (await t.Admin.Get($"{t.W}/provider-connections")).ToJsonString();
        Assert.DoesNotContain("test-key-not-real", conns);
        Assert.Equal("…0000", (string)(await t.Admin.Get($"{t.W}/provider-connections"))[0]!["maskedSuffix"]!);

        var stored = Db(db => (db.ApiKeys.Single(k => k.WorkspaceId == t.Ws), db.ProviderConnections.Single(c => c.WorkspaceId == t.Ws)));
        Assert.Equal(32, stored.Item1.KeyHash.Length);
        Assert.DoesNotContain("test-key-not-real", stored.Item2.Ciphertext);
        Assert.Equal("test-key-not-real-0000000000", f.With(sp => sp.GetRequiredService<SecretProtector>().Unprotect(stored.Item2.Ciphertext)));
    }

    [Fact]
    public async Task Logs_never_contain_secrets_or_prompt_content()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        f.Fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new { id = "r", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1 } }));
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody("gizli-istem-icerigi"))).Status);
        var all = string.Join("\n", f.Logs);
        Assert.NotEmpty(f.Logs);
        foreach (var secret in new[] { t.Key, "test-key-not-real-0000000000", "gizli-istem-icerigi", "Correct-horse-9" })
            Assert.DoesNotContain(secret, all);
    }

    // ---------- Auth ----------

    [Fact]
    public async Task Csrf_is_required_and_login_errors_are_generic()
    {
        var admin = await Setup.Register(f, "csrf@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Send(HttpMethod.Post, "/api/v1/workspaces", new { name = "x" }, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.Send(HttpMethod.Post, "/api/v1/workspaces", new { name = "x" })).StatusCode);

        var anon = new Admin(f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var wrong = await anon.Send(HttpMethod.Post, "/api/v1/auth/login", new { email = "csrf@example.test", password = "bad-password-1" });
        var unknown = await anon.Send(HttpMethod.Post, "/api/v1/auth/login", new { email = "nobody@example.test", password = "bad-password-1" });
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (wrong.StatusCode, unknown.StatusCode));
        Assert.Equal(await wrong.Content.ReadFromJsonAsync<JsonObject>() is { } w ? (string)w["detail"]! : "",
            await unknown.Content.ReadFromJsonAsync<JsonObject>() is { } u ? (string)u["detail"]! : "-");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_is_throttled()
    {
        using var throttled = f.WithWebHostBuilder(b => b.UseSetting("RateLimit:AuthPerMinute", "5"));
        var anon = new Admin(throttled.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) codes.Add((await anon.Send(HttpMethod.Post, "/api/v1/auth/login", new { email = "x@example.test", password = "y-123456789" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public void Production_refuses_demo_and_unprotected_keyring()
    {
        foreach (var (demo, cert) in new[] { ("true", "/x.pfx"), ("false", "") })
        {
            using var prod = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseSetting("Demo:Enabled", demo);
                b.UseSetting("DataProtection:CertificatePath", cert);
            });
            Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        }
    }

    // ---------- Inference ----------

    [Fact]
    public async Task Demo_text_generation_reports_usage_and_unknown_cost_without_price()
    {
        var t = await Setup.Tenant(f);
        var (status, body) = await Setup.Generate(t.Api, Setup.TextBody("Selam"));
        Assert.Equal(200, status);
        Assert.Equal(("completed", "demo", "reported", "unknown"), ((string)body!["status"]!, (string)body["provider"]!,
            (string)body["usage"]!["state"]!, (string)body["cost"]!["state"]!));
        Assert.Null(body["cost"]!["estimatedAmount"]); // unknown price is never zero
        Assert.StartsWith("DEMO", (string)body["output"]!["text"]!);
        // Usage was reported, only the price is missing: settled, not an ambiguous liability awaiting review.
        Assert.Equal(ReservationState.Settled, Db(db => db.Reservations.Single(r => r.WorkspaceId == t.Ws).State));
    }

    [Fact]
    public async Task Unsupported_inputs_are_rejected_before_provider_call()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        f.Fake.Calls.Clear();
        var tools = new { profile = "p", messages = new[] { new { role = "user", content = new object[] { new { type = "text", text = "x" } } } }, tools = new object[0] };
        Assert.Equal((400, "invalid_request"), Pair(await Setup.Generate(t.Api, tools)));
        var audio = new { profile = "p", messages = new[] { new { role = "user", content = new object[] { new { type = "audio" } } } } };
        Assert.Equal((400, "unsupported_capability"), Pair(await Setup.Generate(t.Api, audio)));
        var badSchema = new
        {
            profile = "p", messages = new[] { new { role = "user", content = new object[] { new { type = "text", text = "x" } } } },
            output = new { format = "json_schema", name = "x", schema = new { type = "object", properties = new { a = new { type = "string", pattern = "x" } }, required = new[] { "a" }, additionalProperties = false } },
        };
        Assert.Equal((400, "unsupported_schema"), Pair(await Setup.Generate(t.Api, badSchema)));
        Assert.Equal((400, "invalid_request"), Pair(await Setup.Generate(t.Api, new { profile = "p", messages = new[] { new { role = "user", content = new object[] { new { type = "image", url = "http://evil" } } } } })));
        Assert.Empty(f.Fake.Calls);
    }

    static (int, string) Pair((int Status, JsonNode? Body) r) => (r.Status, Code(r.Body));

    [Fact]
    public async Task Openai_json_schema_generation_validates_output_and_estimates_cost()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        await t.Admin.Post($"{t.W}/pricing", new { provider = "openai", model = "gpt-6-luna", effectiveAt = DateTime.UtcNow.AddMinutes(-1),
            inputPerMillion = 1m, outputPerMillion = 4m, imageInputTokens = 2000, source = "test fixture price" });
        var asset = (string)(await (await Setup.Upload(t.Api, Setup.Png(40, 20))).Content.ReadFromJsonAsync<JsonObject>())!["assetId"]!;
        f.Fake.Calls.Clear();
        var outputs = new Queue<string>(["""{"title":"Plan","days":[],"warnings":[]}""", "{\"title\":1}"]);
        f.Fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new
        {
            id = "resp_x", status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = outputs.Dequeue() } } } },
            usage = new { input_tokens = 1000, output_tokens = 100 },
        }));
        var request = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "examples/workout.request.json")))!;
        request["profile"] = "p";
        request["messages"]![1]!["content"]![1]!["assetId"] = asset;

        var (status, body) = await Setup.Generate(t.Api, request);
        Assert.Equal(200, status);
        Assert.Equal("Plan", (string)body!["output"]!["json"]!["title"]!);
        Assert.Equal(("estimated", 0.0014m), ((string)body["cost"]!["state"]!, (decimal)body["cost"]!["estimatedAmount"]!));
        Assert.Contains("input_image", f.Fake.Calls.Single().Body);
        Assert.Equal("deleted", Db(db => db.Uploads.Single(u => u.Id == Guid.Parse(asset)).State)); // deleted after generation

        var asset2 = (string)(await (await Setup.Upload(t.Api, Setup.Png())).Content.ReadFromJsonAsync<JsonObject>())!["assetId"]!;
        request["messages"]![1]!["content"]![1]!["assetId"] = asset2;
        Assert.Equal((422, "output_validation_failed"), Pair(await Setup.Generate(t.Api, request)));
        // Invalid output still cost money: the attempt keeps its estimated cost.
        Assert.Equal(0.0014m, Db(db => db.Attempts.Where(a => a.WorkspaceId == t.Ws).OrderBy(a => a.StartedAt).Last().EstimatedCost));
    }

    static string Root()
    {
        var d = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(d, "AiGateway.slnx"))) d = Path.GetDirectoryName(d)!;
        return d;
    }

    // ---------- Uploads ----------

    [Fact]
    public async Task Upload_rejects_bad_media_bombs_and_foreign_or_expired_assets()
    {
        var t = await Setup.Tenant(f);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Setup.Upload(t.Api, Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"), name: "a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Setup.Upload(t.Api, Encoding.UTF8.GetBytes("GIF89a....."))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Setup.Upload(t.Api, Setup.Png(5000, 4100))).StatusCode); // > 20 MP
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Setup.Upload(t.Api, new byte[11 << 20])).StatusCode);

        var ok = await (await Setup.Upload(t.Api, Setup.Png())).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(("image/png", 8), ((string)ok!["mimeType"]!, (int)ok["width"]!));

        // Same workspace, other environment: production key cannot use a development asset.
        var prodKey = (string)(await t.Admin.Post($"{t.W}/environments/{t.ProdEnv}/keys", new { name = "prod" }))["key"]!;
        var prodApi = f.CreateClient();
        prodApi.DefaultRequestHeaders.Authorization = new("Bearer", prodKey);
        var imageBody = (string id) => new { profile = "p", messages = new[] { new { role = "user", content = new object[] { new { type = "image", assetId = id } } } } };
        Assert.Equal(HttpStatusCode.NotFound, (await prodApi.DeleteAsync($"/api/v1/uploads/{ok["assetId"]}")).StatusCode);

        f.With(sp => sp.GetRequiredService<GatewayDb>().Uploads.Where(u => u.Id == Guid.Parse((string)ok["assetId"]!))
            .ExecuteUpdate(s => s.SetProperty(u => u.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        Assert.Equal((400, "invalid_request"), Pair(await Setup.Generate(t.Api, imageBody((string)ok["assetId"]!))));
    }

    // ---------- Streaming ----------

    async Task<string> Stream(HttpClient api, object body, CancellationToken ct = default)
    {
        using var res = await api.PostAsJsonAsync("/api/v1/generations", body, ct);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        return await res.Content.ReadAsStringAsync(ct);
    }

    [Fact]
    public async Task Demo_stream_emits_ordered_events_and_one_terminal()
    {
        var t = await Setup.Tenant(f);
        var sse = await Stream(t.Api, Setup.TextBody("akış testi", stream: true));
        Assert.StartsWith("event: started", sse);
        Assert.Contains("event: text.delta", sse);
        Assert.Single(sse.Split("event: completed").Skip(1));
        Assert.DoesNotContain("event: error", sse);
        Assert.Contains("\"sequence\":1", sse);
    }

    [Fact]
    public async Task Provider_error_after_delta_is_partial_and_never_falls_back()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("anthropic", "claude-haiku-5-5",
            fallbacks: conn => [new { provider = "anthropic", model = "claude-sonnet-5-5", connectionId = conn }]));
        f.Fake.Calls.Clear();
        f.Fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Sse(new MemoryStream(Encoding.UTF8.GetBytes(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Yar\"}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"x\"}}\n\n"))));
        var sse = await Stream(t.Api, Setup.TextBody(stream: true));
        Assert.Contains("\"partial\":true", sse);
        Assert.DoesNotContain("event: completed", sse);
        Assert.Single(f.Fake.Calls);
        var req = Db(db => db.Requests.Single(r => r.WorkspaceId == t.Ws));
        Assert.Equal(RequestStatus.Unknown, req.Status); // partial output may be billed
    }

    [Fact]
    public async Task Client_cancel_stops_provider_releases_slot_and_keeps_liability()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        await t.Admin.Ok(HttpMethod.Patch, $"{t.W}/limits", new { scope = "workspace", scopeKey = "", concurrency = 1 });
        var providerCancelled = new TaskCompletionSource();
        f.Fake.Respond = (_, _, _) =>
        {
            var pipe = new System.IO.Pipelines.Pipe();
            var delta = Encoding.UTF8.GetBytes("event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"a\"}\n\n");
            _ = Task.Run(async () =>
            {
                // Keeps streaming until the gateway disposes the provider response (its read side completes).
                while (!(await pipe.Writer.WriteAsync(delta)).IsCompleted) await Task.Delay(100);
                providerCancelled.TrySetResult();
            });
            return Task.FromResult(FakeProvider.Sse(pipe.Reader.AsStream()));
        };
        // Real socket (Kestrel) so a client disconnect reaches HttpContext.RequestAborted.
        using var kestrel = f.WithWebHostBuilder(_ => { });
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        using var http = kestrel.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", t.Key);
        var res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/generations") { Content = JsonContent.Create(Setup.TextBody(stream: true)) },
            HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        while (!(await reader.ReadLineAsync())!.Contains("text.delta")) { }
        res.Dispose(); // client disconnects
        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await WaitFor(() => Db(db => db.Requests.Single(r => r.WorkspaceId == t.Ws).Status) == RequestStatus.Cancelled);
        Assert.Equal(ReservationState.Unknown, Db(db => db.Reservations.Single(r => r.WorkspaceId == t.Ws).State));
        Assert.Equal("cancelled", Db(db => db.Attempts.Single(a => a.WorkspaceId == t.Ws).Status));
        // Slot is free again: next request is not blocked by concurrency.
        f.Fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new { id = "r", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1 } }));
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody())).Status);
    }

    static async Task WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(100);
        Assert.True(cond());
    }

    // ---------- Retry / timeout ----------

    [Fact]
    public async Task Rate_limit_retries_once_then_falls_back_and_keeps_every_attempt()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna", retries: 1,
            fallbacks: conn => [new { provider = "openai", model = "gpt-6-astra", connectionId = conn }]));
        f.Fake.Calls.Clear();
        f.Fake.Respond = (_, body, _) => Task.FromResult(body.Contains("gpt-6-astra")
            ? FakeProvider.Json(new { id = "r", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1 } })
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMilliseconds(10)) } });
        var (status, body) = await Setup.Generate(t.Api, Setup.TextBody());
        Assert.Equal((200, "gpt-6-astra"), (status, (string)body!["model"]!));
        Assert.Equal(3, f.Fake.Calls.Count); // primary + one retry + fallback, no SDK multiplication
        Assert.Equal(3, Db(db => db.Attempts.Count(a => a.WorkspaceId == t.Ws)));
    }

    [Fact]
    public async Task Timeout_is_ambiguous_not_retried_and_keeps_liability()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna", retries: 1, timeout: 5));
        await t.Admin.Post($"{t.W}/pricing", new { provider = "openai", model = "gpt-6-luna", effectiveAt = DateTime.UtcNow.AddMinutes(-1),
            inputPerMillion = 1m, outputPerMillion = 1m, source = "test" });
        f.Fake.Calls.Clear();
        f.Fake.Respond = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        var (status, body) = await Setup.Generate(t.Api, Setup.TextBody());
        Assert.Equal((504, "provider_timeout"), (status, Code(body)));
        Assert.Single(f.Fake.Calls);
        var bucket = Db(db => db.Buckets.Single(b => b.WorkspaceId == t.Ws && b.Scope == "workspace"));
        Assert.True(bucket.Unknown > 0);
        Assert.Equal(0, bucket.Reserved);
    }

    // ---------- Budget ----------

    [Fact]
    public async Task Fifty_concurrent_reservations_never_exceed_budget()
    {
        var t = await Setup.Tenant(f);
        await t.Admin.Ok(HttpMethod.Patch, $"{t.W}/limits", new { scope = "workspace", scopeKey = "", monthlyUsd = 1.0m });
        await t.Admin.Ok(HttpMethod.Patch, $"{t.W}/limits", new { scope = "project", scopeKey = t.Project.ToString(), monthlyUsd = 0.5m });
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDb>();
            var req = new GenerationRequest { WorkspaceId = t.Ws, ProjectId = t.Project, EnvironmentId = t.DevEnv, ProfileRevisionId = Guid.Empty };
            db.Database.ExecuteSql($"""INSERT INTO "Requests" ("Id","WorkspaceId","ProjectId","EnvironmentId","ProfileRevisionId","Stream","Status","CreatedAt") VALUES ({req.Id},{t.Ws},{t.Project},{t.DevEnv},{Guid.Empty},false,'in_progress',now())""");
            try { await new BudgetService(db).ReserveAsync(t.Ws, t.Project, $"u{i % 3}", req.Id, 0.03m, 10, 60, true); return true; }
            catch (GatewayException e) when (e.Code == "budget_exceeded") { return false; }
        })));
        Assert.Equal(16, results.Count(ok => ok)); // floor(0.5 / 0.03) on the tighter project scope
        var buckets = Db(db => db.Buckets.Where(b => b.WorkspaceId == t.Ws).ToList());
        Assert.All(buckets.Where(b => b.Scope != "endUser"), b => Assert.Equal(0.48m, b.Reserved));
        Assert.Equal(0.48m, buckets.Where(b => b.Scope == "endUser").Sum(b => b.Reserved));
    }

    [Fact]
    public async Task Settlement_is_idempotent_and_unknown_price_blocks_budgeted_calls()
    {
        var t = await Setup.Tenant(f);
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody())).Status);
        var id = Db(db => db.Requests.Single(r => r.WorkspaceId == t.Ws).Id);
        var before = Db(db => db.Buckets.Single(b => b.WorkspaceId == t.Ws && b.Scope == "workspace"));
        await f.With(sp => sp.GetRequiredService<BudgetService>().SettleAsync(id, ReservationState.Settled, 5m, 999));
        var after = Db(db => db.Buckets.Single(b => b.WorkspaceId == t.Ws && b.Scope == "workspace"));
        Assert.Equal((before.Settled, before.Tokens, before.Requests), (after.Settled, after.Tokens, after.Requests));

        await t.Admin.Ok(HttpMethod.Patch, $"{t.W}/limits", new { scope = "workspace", scopeKey = "", monthlyUsd = 10m });
        Assert.Equal((503, "pricing_unavailable"), Pair(await Setup.Generate(t.Api, Setup.TextBody())));
    }

    [Fact]
    public async Task End_user_request_quota_and_failed_dispatch_release()
    {
        var t = await Setup.Tenant(f);
        await t.Admin.Ok(HttpMethod.Patch, $"{t.W}/limits", new { scope = "endUser", scopeKey = "*", monthlyRequests = 2 });
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody(endUser: "u1"))).Status);
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody(endUser: "u1"))).Status);
        Assert.Equal((429, "rate_limit_exceeded"), Pair(await Setup.Generate(t.Api, Setup.TextBody(endUser: "u1"))));
        Assert.Equal(200, (await Setup.Generate(t.Api, Setup.TextBody(endUser: "u2"))).Status);
    }

    [Fact]
    public async Task Price_version_change_keeps_historical_costs()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        var price = new { provider = "openai", model = "gpt-6-luna", inputPerMillion = 1m, outputPerMillion = 1m, source = "v1" };
        await t.Admin.Post($"{t.W}/pricing", new { price.provider, price.model, effectiveAt = DateTime.UtcNow.AddMinutes(-1), price.inputPerMillion, price.outputPerMillion, price.source });
        f.Fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new { id = "r", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1_000_000, output_tokens = 0 } }));
        var first = (await Setup.Generate(t.Api, Setup.TextBody())).Body!;
        await t.Admin.Post($"{t.W}/pricing", new { price.provider, price.model, effectiveAt = DateTime.UtcNow, inputPerMillion = 9m, outputPerMillion = 9m, source = "v2" });
        var second = (await Setup.Generate(t.Api, Setup.TextBody())).Body!;
        Assert.Equal((1m, 9m), ((decimal)first["cost"]!["estimatedAmount"]!, (decimal)second["cost"]!["estimatedAmount"]!));
        var usage = await t.Admin.Get($"{t.W}/usage");
        Assert.Equal(10m, (decimal)usage["summary"]!["estimatedUsd"]!);
        Assert.Contains(usage["items"]!.AsArray(), i => (decimal?)i!["estimatedCost"] == 1m);
    }

    // ---------- Idempotency ----------

    [Fact]
    public async Task Idempotent_replay_makes_no_new_call_and_body_change_conflicts()
    {
        var t = await Setup.Tenant(f, Setup.WithConnection("openai", "gpt-6-luna"));
        f.Fake.Calls.Clear();
        f.Fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new { id = "r", status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "bir" } } } }, usage = new { input_tokens = 1, output_tokens = 1 } }));
        var first = await Setup.Generate(t.Api, Setup.TextBody("x"), "idem-1");
        var replay = await Setup.Generate(t.Api, Setup.TextBody("x"), "idem-1");
        Assert.Equal(first.Body!.ToJsonString(), replay.Body!.ToJsonString());
        Assert.Single(f.Fake.Calls);
        Assert.Equal((409, "idempotency_conflict"), Pair(await Setup.Generate(t.Api, Setup.TextBody("y"), "idem-1")));

        f.Fake.Respond = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        var slow = Setup.Generate(t.Api, Setup.TextBody("z"), "idem-2");
        await WaitFor(() => Db(db => db.Requests.Any(r => r.IdempotencyKey == "idem-2")));
        Assert.Equal((409, "request_in_progress"), Pair(await Setup.Generate(t.Api, Setup.TextBody("z"), "idem-2")));
        Assert.Equal(504, (await slow).Status);
        // Unknown outcome is never regenerated.
        Assert.Equal((409, "idempotency_conflict"), Pair(await Setup.Generate(t.Api, Setup.TextBody("z"), "idem-2")));
        Assert.Equal(2, f.Fake.Calls.Count);
    }

    // ---------- Reconciliation ----------

    [Fact]
    public async Task Reconciler_recovers_crashed_requests_and_cleans_uploads()
    {
        var t = await Setup.Tenant(f);
        var asset = (string)(await (await Setup.Upload(t.Api, Setup.Png())).Content.ReadFromJsonAsync<JsonObject>())!["assetId"]!;
        await f.With(async sp =>
        {
            var db = sp.GetRequiredService<GatewayDb>();
            var budget = sp.GetRequiredService<BudgetService>();
            var rev = db.ProfileRevisions.First(r => r.WorkspaceId == t.Ws).Id;
            var crashed = new GenerationRequest { WorkspaceId = t.Ws, ProjectId = t.Project, EnvironmentId = t.DevEnv, ProfileRevisionId = rev, CreatedAt = DateTime.UtcNow.AddHours(-1) };
            db.Requests.Add(crashed);
            await db.SaveChangesAsync();
            await budget.ReserveAsync(t.Ws, t.Project, null, crashed.Id, 0.2m, 1, 60, false);
            db.Attempts.Add(new ProviderAttempt { WorkspaceId = t.Ws, RequestId = crashed.Id, AttemptNumber = 1, Provider = "demo", Model = "demo-model" });
            await db.SaveChangesAsync();
            await db.Uploads.Where(u => u.Id == Guid.Parse(asset)).ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

            await Reconciler.RunOnceAsync(db, budget, sp.GetRequiredService<IObjectStorage>(), DateTime.UtcNow);
            await Reconciler.RunOnceAsync(db, budget, sp.GetRequiredService<IObjectStorage>(), DateTime.UtcNow); // idempotent

            db.ChangeTracker.Clear();
            Assert.Equal(RequestStatus.Unknown, db.Requests.Single(r => r.Id == crashed.Id).Status);
            Assert.Equal(ReservationState.ReviewRequired, db.Reservations.Single(r => r.RequestId == crashed.Id).State);
            var bucket = db.Buckets.Single(b => b.WorkspaceId == t.Ws && b.Scope == "workspace");
            Assert.Equal((0m, 0.2m), (bucket.Reserved, bucket.Unknown)); // TTL never erases liability
            Assert.Equal("deleted", db.Uploads.Single(u => u.Id == Guid.Parse(asset)).State);
            return 0;
        });
    }

    // ---------- Admin flows ----------

    [Fact]
    public async Task Playground_uses_same_service_and_usage_reports_it()
    {
        var t = await Setup.Tenant(f);
        var pg = $"{t.W}/environments/{t.DevEnv}";
        var form = new MultipartFormDataContent { { new ByteArrayContent(Setup.Png()), "file", "a.png" } };
        await t.Admin.RefreshCsrf();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{pg}/uploads") { Content = form };
        var csrf = (string)(await t.Admin.Http.GetFromJsonAsync<JsonObject>("/api/v1/auth/csrf"))!["token"]!;
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.Created, (await t.Admin.Http.SendAsync(req)).StatusCode);
        var res = await t.Admin.Post($"{pg}/generations", Setup.TextBody("playground"));
        Assert.Equal("completed", (string)res["status"]!);
        var usage = await t.Admin.Get($"{t.W}/usage?limit=1");
        Assert.Equal(1, (int)usage["summary"]!["requests"]!);
        Assert.Equal(1, (int)usage["summary"]!["unknownCostRequests"]!);
        var detail = await t.Admin.Get($"{t.W}/requests/{usage["items"]![0]!["requestId"]}");
        Assert.Equal("succeeded", (string)detail["attempts"]![0]!["status"]!);
        Assert.NotEmpty((await t.Admin.Get($"{t.W}/audit"))["items"]!.AsArray());
    }

    [Fact]
    public async Task Profile_revision_rejects_unverified_models_and_missing_capabilities()
    {
        var t = await Setup.Tenant(f);
        var url = $"{t.W}/profiles/{t.ProfileId}/revisions";
        var bad = await t.Admin.Send(HttpMethod.Post, url, new { config = Setup.Config("openai", "gpt-made-up", null) });
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        var noConn = await t.Admin.Send(HttpMethod.Post, url, new { config = Setup.Config("openai", "gpt-6-luna", Guid.NewGuid()) });
        Assert.Equal(HttpStatusCode.BadRequest, noConn.StatusCode);
    }
}

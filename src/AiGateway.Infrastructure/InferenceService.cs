using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using AiGateway.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AiGateway.Infrastructure;

public record Caller(Guid WorkspaceId, Guid ProjectId, Guid EnvironmentId, Guid? ApiKeyId, Guid? UserId);

public record Candidate(ModelRef Ref, string ApiKey, PriceVersion? Price);

public class Prepared
{
    public required GenerationRequest Request { get; init; }
    public required ProfileConfig Config { get; init; }
    public required List<Candidate> Candidates { get; init; }
    public required ProviderRequest ProviderRequest { get; init; }
    public required List<UploadAsset> Assets { get; init; }
    public JsonElement? Schema { get; init; }
    public int InputEstimate { get; init; }
}

public class InferenceService(GatewayDb db, BudgetService budget, ProviderRegistry providers, IObjectStorage storage,
    SecretProtector secrets, IConfiguration config)
{
    public static readonly Meter Meter = new("AiGateway");
    static readonly Counter<long> Requests = Meter.CreateCounter<long>("gateway.requests");
    static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gateway.request.duration", "ms");
    static readonly Histogram<double> Ttft = Meter.CreateHistogram<double>("gateway.ttft", "ms");
    static readonly Counter<long> Tokens = Meter.CreateCounter<long>("gateway.tokens");
    static readonly HashSet<string> RolesAllowed = ["system", "user", "assistant"];

    int MaxImages => config.GetValue("Limits:MaxImagesPerRequest", 4);

    public static byte[] Hash(GenerationBody body) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body));

    /// <returns>Prepared request, or a stored response JSON for an idempotent replay.</returns>
    public async Task<(Prepared? Prepared, string? Replay)> PrepareAsync(Caller caller, GenerationBody body, string? idempotencyKey, CancellationToken ct)
    {
        Validate(body, idempotencyKey);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == caller.WorkspaceId && p.Name == body.Profile, ct)
            ?? throw GatewayException.NotFound();
        var binding = await db.Bindings.AsNoTracking().SingleOrDefaultAsync(b =>
            b.WorkspaceId == caller.WorkspaceId && b.EnvironmentId == caller.EnvironmentId && b.ProfileId == profile.Id, ct)
            ?? throw new GatewayException(404, "not_found", "Profil bu ortama yayınlanmamış.");
        var revision = await db.ProfileRevisions.AsNoTracking().SingleAsync(r => r.WorkspaceId == caller.WorkspaceId && r.Id == binding.RevisionId, ct);
        var cfg = JsonSerializer.Deserialize<ProfileConfig>(revision.ConfigJson, JsonSerializerOptions.Web)!;

        var maxOut = body.MaxOutputTokens ?? cfg.OutputTokenLimit;
        if (maxOut <= 0 || maxOut > cfg.OutputTokenLimit)
            throw GatewayException.Invalid($"maxOutputTokens 1..{cfg.OutputTokenLimit} aralığında olmalı.");

        var images = body.Messages.SelectMany(m => m.Content).Count(c => c.Type == "image");
        var schema = body.Output?.Format == "json_schema" ? body.Output.Schema : null;
        var needed = cfg.RequiredCapabilities.Concat(["text"])
            .Concat(images > 0 ? ["image"] : []).Concat(schema is null ? [] : ["jsonSchema"]).Concat(body.Stream ? ["streaming"] : [])
            .ToHashSet();

        var candidates = new List<Candidate>();
        foreach (var (m, i) in new[] { cfg.Primary }.Concat(cfg.Fallbacks).Select((m, i) => (m, i)))
        {
            var cap = await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.Provider == m.Provider && x.Model == m.Model, ct);
            if (cap is null)
            {
                if (i == 0) throw new GatewayException(403, "model_not_allowed", $"Model doğrulanmış listede değil: {m.Provider}/{m.Model}");
                continue;
            }
            var missing = needed.Where(n => !(n switch
            {
                "text" => cap.Text, "image" => cap.Image, "streaming" => cap.Streaming, "jsonSchema" => cap.JsonSchema, _ => false,
            })).ToList();
            if (missing.Count > 0)
            {
                if (i == 0) throw new GatewayException(400, "unsupported_capability", $"Model şu yetenekleri desteklemiyor: {string.Join(", ", missing)}");
                continue;
            }
            if (schema is { } s && cfg.SchemaMode == "native") SchemaPolicy.Check(s, m.Provider == "openai");
            providers.Get(m.Provider); // throws when provider disabled (e.g. demo in production)
            var apiKey = "";
            if (m.Provider != "demo")
            {
                var conn = await db.ProviderConnections.AsNoTracking().SingleOrDefaultAsync(c =>
                    c.WorkspaceId == caller.WorkspaceId && c.Id == m.ConnectionId && c.Provider == m.Provider, ct)
                    ?? throw GatewayException.Invalid($"{m.Provider} için sağlayıcı bağlantısı yok.");
                apiKey = secrets.Unprotect(conn.Ciphertext);
            }
            var price = await db.Prices.AsNoTracking()
                .Where(p => p.WorkspaceId == caller.WorkspaceId && p.Provider == m.Provider && p.Model == m.Model && p.EffectiveAt <= DateTime.UtcNow)
                .OrderByDescending(p => p.EffectiveAt).FirstOrDefaultAsync(ct);
            candidates.Add(new(m, apiKey, price));
        }
        if (schema is { } sc && cfg.SchemaMode != "native") SchemaPolicy.Check(sc, false);

        var chars = body.Messages.SelectMany(m => m.Content).Sum(c => c.Text?.Length ?? 0) + (schema?.GetRawText().Length ?? 0);
        var inputEstimate = Pricing.EstimateTextTokens(chars);
        decimal? amount = 0;
        foreach (var c in candidates) // every possible attempt is reserved: retries and fallbacks keep their cost
            amount = c.Price is null || (images > 0 && c.Price.ImageInputTokens is null) ? null
                : amount + Pricing.Cost(c.Price, inputEstimate + images * (c.Price.ImageInputTokens ?? 0), maxOut, 0) * (1 + cfg.AutomaticRetryCount);

        var request = new GenerationRequest
        {
            WorkspaceId = caller.WorkspaceId, ProjectId = caller.ProjectId, EnvironmentId = caller.EnvironmentId,
            ApiKeyId = caller.ApiKeyId, UserId = caller.UserId, EndUserRef = body.EndUserRef, ProfileRevisionId = revision.Id,
            Feature = body.Metadata?.GetValueOrDefault("feature"), Stream = body.Stream,
            IdempotencyKey = idempotencyKey, RequestHash = idempotencyKey is null ? null : Hash(body),
            Provider = cfg.Primary.Provider, Model = cfg.Primary.Model,
        };
        db.Requests.Add(request);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            return (null, await ReplayAsync(caller, idempotencyKey!, request.RequestHash!, ct));
        }
        db.ChangeTracker.Clear();

        try
        {
            await budget.ReserveAsync(caller.WorkspaceId, caller.ProjectId, body.EndUserRef, request.Id, amount,
                inputEstimate + maxOut, cfg.TimeoutSeconds + 30, cfg.PricingRequiredForBudgetEnforcement);
        }
        catch (Exception e)
        {
            await Fail(request, e is GatewayException g ? g.Code : "internal_error");
            throw;
        }

        List<UploadAsset> assets = [];
        ProviderRequest providerRequest;
        try
        {
            assets = await ClaimAssetsAsync(caller, body, ct);
            var bytes = new Dictionary<Guid, byte[]>();
            foreach (var a in assets) bytes[a.Id] = await storage.GetAsync(a.ObjectKey, ct);
            providerRequest = new ProviderRequest("", body.Messages.Select(m => new ProviderMessage(m.Role, m.Content.Select(c =>
                c.Type == "image"
                    ? new ProviderPart(null, bytes[c.AssetId!.Value], assets.First(a => a.Id == c.AssetId).MimeType)
                    : new ProviderPart(c.Text)).ToList())).ToList(),
                maxOut, body.Output?.Name, cfg.SchemaMode == "native" ? schema : null);
        }
        catch (Exception e)
        {
            await budget.SettleAsync(request.Id, ReservationState.Released, 0, 0);
            await Fail(request, e is GatewayException g ? g.Code : "internal_error");
            await DeleteAssetsAsync(assets);
            throw;
        }
        return (new Prepared
        {
            Request = request, Config = cfg, Candidates = candidates, ProviderRequest = providerRequest, Assets = assets,
            Schema = schema, InputEstimate = inputEstimate,
        }, null);
    }

    void Validate(GenerationBody b, string? idem)
    {
        if (idem is { Length: 0 or > 128 }) throw GatewayException.Invalid("Idempotency-Key 1..128 karakter olmalı.");
        if (string.IsNullOrWhiteSpace(b.Profile)) throw GatewayException.Invalid("profile zorunlu.");
        if (b.Messages is not { Count: > 0 and <= 50 }) throw GatewayException.Invalid("messages 1..50 öğe içermeli.");
        if (b.EndUserRef is { Length: > 128 }) throw GatewayException.Invalid("endUserRef en fazla 128 karakter.");
        if (b.Metadata is { } md && (md.Count > 10 || md.Any(kv => kv.Key.Length > 64 || kv.Value is null or { Length: > 256 })))
            throw GatewayException.Invalid("metadata en fazla 10 öğe, değerler en fazla 256 karakter.");
        var chars = 0;
        foreach (var m in b.Messages)
        {
            if (!RolesAllowed.Contains(m.Role)) throw GatewayException.Invalid($"Geçersiz rol: {m.Role}");
            if (m.Content is not { Count: > 0 }) throw GatewayException.Invalid("content boş olamaz.");
            foreach (var c in m.Content)
            {
                if (c.Type == "text" && c.Text is not null) chars += c.Text.Length;
                else if (c.Type == "image" && c.AssetId is not null && m.Role == "user") continue;
                else if (c.Type is "text" or "image") throw GatewayException.Invalid("Geçersiz içerik bloğu (görseller yalnızca user mesajında, assetId ile).");
                else throw new GatewayException(400, "unsupported_capability", $"Desteklenmeyen içerik türü: {c.Type}");
            }
        }
        if (chars > 200_000) throw GatewayException.Invalid("Toplam metin 200.000 karakteri aşıyor.");
        if (b.Messages.SelectMany(m => m.Content).Count(c => c.Type == "image") > MaxImages)
            throw GatewayException.Invalid($"İstek başına en fazla {MaxImages} görsel.");
        switch (b.Output?.Format)
        {
            case null or "text": break;
            case "json_schema" when b.Output.Schema is { ValueKind: JsonValueKind.Object }: break;
            case "json_schema": throw new GatewayException(400, "unsupported_schema", "json_schema için schema nesnesi zorunlu.");
            default: throw GatewayException.Invalid("output.format text veya json_schema olmalı.");
        }
    }

    async Task<string> ReplayAsync(Caller caller, string key, byte[] hash, CancellationToken ct)
    {
        var prior = await db.Requests.AsNoTracking().SingleAsync(r => r.WorkspaceId == caller.WorkspaceId &&
            r.EnvironmentId == caller.EnvironmentId && r.ApiKeyId == caller.ApiKeyId && r.IdempotencyKey == key, ct);
        if (!prior.RequestHash!.SequenceEqual(hash))
            throw new GatewayException(409, "idempotency_conflict", "Aynı Idempotency-Key farklı gövdeyle kullanıldı.");
        if (prior.Status == RequestStatus.InProgress)
            throw new GatewayException(409, "request_in_progress", "Aynı anahtarlı istek sürüyor.", true, TimeSpan.FromSeconds(2));
        if (prior.Status == RequestStatus.Completed && !prior.Stream && prior.ResponseJson is not null) return prior.ResponseJson;
        // Unknown, failed or streamed outcomes are never regenerated under the same key.
        throw new GatewayException(409, "idempotency_conflict", $"Önceki istek sonucu tekrar oynatılamaz (durum: {prior.Status}).");
    }

    async Task<List<UploadAsset>> ClaimAssetsAsync(Caller caller, GenerationBody body, CancellationToken ct)
    {
        var ids = body.Messages.SelectMany(m => m.Content).Where(c => c.Type == "image").Select(c => c.AssetId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        var now = DateTime.UtcNow;
        // Atomic claim: cleanup only deletes "available" assets, so claimed bytes cannot vanish mid-read.
        var claimed = await db.Uploads.Where(u => ids.Contains(u.Id) && u.WorkspaceId == caller.WorkspaceId &&
                u.EnvironmentId == caller.EnvironmentId && u.State == "available" && u.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, "claimed"), ct);
        var assets = await db.Uploads.AsNoTracking().Where(u => ids.Contains(u.Id) && u.WorkspaceId == caller.WorkspaceId &&
            u.EnvironmentId == caller.EnvironmentId && u.State == "claimed").ToListAsync(ct);
        if (claimed != ids.Count)
        {
            await DeleteAssetsAsync(assets.Where(a => ids.Contains(a.Id)).ToList());
            throw GatewayException.Invalid("Görsel bulunamadı, süresi doldu veya zaten kullanıldı.");
        }
        return assets;
    }

    async Task DeleteAssetsAsync(List<UploadAsset> assets)
    {
        foreach (var a in assets)
        {
            try { await storage.DeleteAsync(a.ObjectKey, CancellationToken.None); }
            catch (Exception) { continue; } // reconciler retries objects whose rows stay "claimed"
            await db.Uploads.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.State, "deleted"));
        }
    }

    async Task Fail(GenerationRequest r, string code, string status = RequestStatus.Failed) =>
        await db.Requests.Where(x => x.Id == r.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, status).SetProperty(x => x.ErrorCode, code)
            .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
            .SetProperty(x => x.DurationMs, (int)(DateTime.UtcNow - r.CreatedAt).TotalMilliseconds));

    /// <summary>
    /// Runs attempts with at most one retry layer. onDelta != null means streaming: once a delta has been
    /// emitted no retry or fallback happens. Always settles the reservation exactly once.
    /// </summary>
    public async Task<GenerationResponse> RunAsync(Prepared p, Func<string, Task>? onDelta, CancellationToken clientCt)
    {
        var started = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
        deadline.CancelAfter(TimeSpan.FromSeconds(p.Config.TimeoutSeconds));
        var attempts = new List<ProviderAttempt>();
        var sawOutput = false;
        GatewayException? error = null;
        GenerationResponse? response = null;
        var status = RequestStatus.Failed;

        try
        {
            var n = 0;
            foreach (var c in p.Candidates)
            {
                for (var retry = 0; retry <= Math.Min(p.Config.AutomaticRetryCount, 1); retry++)
                {
                    var attempt = new ProviderAttempt
                    {
                        WorkspaceId = p.Request.WorkspaceId, RequestId = p.Request.Id, AttemptNumber = ++n,
                        Provider = c.Ref.Provider, Model = c.Ref.Model, PriceVersionId = c.Price?.Id,
                    };
                    db.Attempts.Add(attempt);
                    await db.SaveChangesAsync(CancellationToken.None); // attempt recorded before dispatch
                    attempts.Add(attempt);
                    try
                    {
                        var req = p.ProviderRequest with { Model = c.Ref.Model };
                        var adapter = providers.Get(c.Ref.Provider);
                        ProviderResult result;
                        if (onDelta is null) result = await adapter.GenerateAsync(req, c.ApiKey, deadline.Token);
                        else
                        {
                            var text = new System.Text.StringBuilder();
                            result = null!;
                            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                            var idleTimeout = TimeSpan.FromSeconds(config.GetValue("Limits:StreamIdleSeconds", 30));
                            idle.CancelAfter(idleTimeout);
                            await foreach (var ev in adapter.StreamAsync(req, c.ApiKey, idle.Token))
                            {
                                idle.CancelAfter(idleTimeout);
                                if (ev.Delta is { Length: > 0 } d)
                                {
                                    if (!sawOutput) { attempt.TtftMs = (int)started.ElapsedMilliseconds; Ttft.Record(started.ElapsedMilliseconds); }
                                    sawOutput = true;
                                    text.Append(d);
                                    await onDelta(d);
                                }
                                if (ev.Final is { } f) result = f with { Text = text.ToString() };
                            }
                        }
                        Record(attempt, "succeeded", result.Usage, c.Price, result.ProviderRequestId);
                        var json = p.Schema is { } schema ? SchemaPolicy.ParseAndValidate(result.Text, schema) : (JsonElement?)null;
                        response = new GenerationResponse(p.Request.Id, RequestStatus.Completed, p.Request.ProfileRevisionId,
                            c.Ref.Provider, c.Ref.Model, new OutputResult(json is null ? result.Text : null, json),
                            Usage(attempts), Cost(attempts), result.FinishReason);
                        status = RequestStatus.Completed;
                        return response;
                    }
                    catch (ProviderException e) when (e.Kind == "rejected" && !sawOutput)
                    {
                        Record(attempt, "failed", null, c.Price, null, e.RateLimited ? "rate_limited" : "provider_unavailable", notCharged: true);
                        error = e.RateLimited
                            ? new GatewayException(429, "rate_limit_exceeded", "Sağlayıcı hız sınırı.", true, e.RetryAfter)
                            : new GatewayException(503, "provider_unavailable", e.Message, true);
                        var wait = e.RetryAfter ?? TimeSpan.FromSeconds(1);
                        if (!e.RateLimited || started.Elapsed + wait >= TimeSpan.FromSeconds(p.Config.TimeoutSeconds)) break; // to fallback
                        await Task.Delay(wait, deadline.Token);
                    }
                    catch (ProviderException e) when (e.Kind == "failed")
                    {
                        Record(attempt, "failed", null, c.Price, null, "provider_error", notCharged: !sawOutput);
                        throw new GatewayException(502, "provider_error", e.Message);
                    }
                    catch (Exception e) when (e is OperationCanceledException or ProviderException)
                    {
                        // Timeout, cancellation or broken connection: the provider may still bill → unknown liability.
                        var cancelled = clientCt.IsCancellationRequested;
                        Record(attempt, cancelled ? "cancelled" : "unknown", null, c.Price, null, cancelled ? "client_cancelled" : "provider_timeout");
                        status = cancelled ? RequestStatus.Cancelled : RequestStatus.Unknown;
                        throw cancelled
                            ? new GatewayException(499, "client_cancelled", "İstemci isteği iptal etti.")
                            : new GatewayException(504, "provider_timeout", "Sağlayıcı zaman aşımına uğradı; ücretlendirilmiş olabilir.", false);
                    }
                }
                if (sawOutput) break;
            }
            throw error ?? new GatewayException(503, "provider_unavailable", "Uygun sağlayıcı yok.");
        }
        catch (GatewayException e) { error = e; throw; }
        finally
        {
            await FinishAsync(p, attempts, status, error, response, started.ElapsedMilliseconds, onDelta is not null);
        }
    }

    static void Record(ProviderAttempt a, string status, ProviderUsage? usage, PriceVersion? price, string? providerId,
        string? error = null, bool notCharged = false)
    {
        a.Status = status;
        a.ErrorCode = error;
        a.ProviderRequestId = providerId;
        a.EndedAt = DateTime.UtcNow;
        if (usage is not null)
        {
            (a.InputTokens, a.OutputTokens, a.CachedInputTokens) = (usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens);
            a.UsageState = usage.Reported ? "reported" : "unknown";
        }
        if (notCharged) (a.UsageState, a.InputTokens, a.OutputTokens, a.EstimatedCost, a.CostState) = ("reported", 0, 0, 0m, "estimated");
        else if (a.UsageState == "reported" && price is not null)
            (a.EstimatedCost, a.CostState) = (Pricing.Cost(price, a.InputTokens!.Value, a.OutputTokens!.Value, a.CachedInputTokens ?? 0), "estimated");
    }

    static UsageDto Usage(List<ProviderAttempt> a) => a.All(x => x.UsageState == "reported")
        ? new(a.Sum(x => x.InputTokens), a.Sum(x => x.OutputTokens), a.Sum(x => x.CachedInputTokens ?? 0), "reported")
        : new(null, null, null, "unknown");

    static CostDto Cost(List<ProviderAttempt> a) => a.All(x => x.CostState == "estimated")
        ? new("USD", a.Sum(x => x.EstimatedCost), "estimated", a.LastOrDefault(x => x.PriceVersionId is not null)?.PriceVersionId)
        : new("USD", null, "unknown", null);

    async Task FinishAsync(Prepared p, List<ProviderAttempt> attempts, string status, GatewayException? error,
        GenerationResponse? response, long ms, bool stream)
    {
        var none = CancellationToken.None;
        try { await db.SaveChangesAsync(none); }
        finally { db.ChangeTracker.Clear(); }
        // Ambiguous outcome (missing usage) keeps the reservation as liability; reported-but-unpriced usage settles with cost state unknown.
        var unknown = attempts.Any(a => a.UsageState == "unknown");
        var outcome = attempts.Count == 0 ? ReservationState.Released : unknown ? ReservationState.Unknown : ReservationState.Settled;
        await budget.SettleAsync(p.Request.Id, outcome, attempts.Sum(a => a.EstimatedCost ?? 0),
            attempts.Sum(a => (long)(a.InputTokens ?? 0) + (a.OutputTokens ?? 0)));
        var last = attempts.LastOrDefault();
        await db.Requests.Where(x => x.Id == p.Request.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.ErrorCode, error == null ? null : error.Code)
            .SetProperty(x => x.Provider, last == null ? p.Request.Provider : last.Provider)
            .SetProperty(x => x.Model, last == null ? p.Request.Model : last.Model)
            .SetProperty(x => x.ResponseJson, response == null || stream ? null : JsonSerializer.Serialize(response, JsonDefaults.Options))
            .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
            .SetProperty(x => x.DurationMs, (int)ms), none);
        await DeleteAssetsAsync(p.Assets);
        var tags = new TagList { { "provider", last?.Provider ?? "none" }, { "status", status } };
        Requests.Add(1, tags);
        Duration.Record(ms, tags);
        Tokens.Add(attempts.Sum(a => (long)(a.InputTokens ?? 0) + (a.OutputTokens ?? 0)), tags);
    }
}

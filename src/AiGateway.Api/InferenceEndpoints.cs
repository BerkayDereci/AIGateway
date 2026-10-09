using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiGateway.Core;
using AiGateway.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiGateway.Api;

public static class InferenceEndpoints
{
    public static void Map(WebApplication app)
    {
        // Project API key routes: Bearer only, cookies are ignored.
        var v1 = app.MapGroup("/api/v1");
        v1.MapPost("/uploads", async (HttpContext c, GatewayDb db) => await Upload(await KeyCaller(c, db), c, db));
        v1.MapDelete("/uploads/{assetId:guid}", async (Guid assetId, HttpContext c, GatewayDb db) => await DeleteUpload(await KeyCaller(c, db), assetId, c, db));
        v1.MapPost("/generations", async (HttpContext c, GatewayDb db) =>
        {
            var caller = await KeyCaller(c, db);
            await Generate(caller, await ReadBody(c), c.Request.Headers["Idempotency-Key"].FirstOrDefault(), c);
        });

        // Admin playground: cookie + CSRF, owner/admin, same inference service and budget rules; no API key exposed to the UI.
        var pg = app.MapGroup("/api/v1/workspaces/{workspaceId:guid}/environments/{environmentId:guid}")
            .RequireAuthorization().AddEndpointFilter(AdminEndpoints.Csrf);
        pg.MapPost("/uploads", async (Guid workspaceId, Guid environmentId, HttpContext c, GatewayDb db) =>
            await Upload(await PlaygroundCaller(workspaceId, environmentId, c, db), c, db));
        pg.MapPost("/generations", async (Guid workspaceId, Guid environmentId, HttpContext c, GatewayDb db) =>
        {
            var caller = await PlaygroundCaller(workspaceId, environmentId, c, db);
            await Generate(caller, await ReadBody(c), null, c);
        });
    }

    static async Task<GenerationBody> ReadBody(HttpContext c)
    {
        var options = c.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        try { return await c.Request.ReadFromJsonAsync<GenerationBody>(options) ?? throw GatewayException.Invalid("Gövde boş."); }
        catch (JsonException) { throw GatewayException.Invalid("Geçersiz JSON veya desteklenmeyen alan (tools/audio vb. V1'de yok)."); }
    }

    public static async Task<Caller> KeyCaller(HttpContext c, GatewayDb db)
    {
        var invalid = new GatewayException(401, "invalid_key", "API anahtarı geçersiz, iptal edilmiş veya süresi dolmuş.");
        var header = c.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length < 7 + 20) throw invalid;
        var secret = header[7..].Trim();
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Prefix == ApiKeys.PrefixOf(secret));
        if (key is null || !CryptographicOperations.FixedTimeEquals(key.KeyHash, SHA256.HashData(Encoding.UTF8.GetBytes(secret)))
            || key.RevokedAt is not null || key.ExpiresAt <= DateTime.UtcNow) throw invalid;
        var env = await db.Environments.AsNoTracking().SingleAsync(e => e.WorkspaceId == key.WorkspaceId && e.Id == key.EnvironmentId);
        if (await db.Projects.AnyAsync(p => p.WorkspaceId == key.WorkspaceId && p.Id == env.ProjectId && p.ArchivedAt != null)) throw invalid;
        return new Caller(key.WorkspaceId, env.ProjectId, env.Id, key.Id, null);
    }

    static async Task<Caller> PlaygroundCaller(Guid ws, Guid envId, HttpContext c, GatewayDb db)
    {
        await AdminEndpoints.Require(db, c, ws, Roles.Owner, Roles.Admin);
        var env = await db.Environments.AsNoTracking().SingleOrDefaultAsync(e => e.WorkspaceId == ws && e.Id == envId) ?? throw GatewayException.NotFound();
        return new Caller(ws, env.ProjectId, env.Id, null, AdminEndpoints.UserId(c));
    }

    static async Task<IResult> Upload(Caller caller, HttpContext c, GatewayDb db)
    {
        var cfg = c.RequestServices.GetRequiredService<IConfiguration>();
        var storage = c.RequestServices.GetRequiredService<IObjectStorage>();
        if (!c.Request.HasFormContentType) throw new GatewayException(415, "unsupported_media_type", "multipart/form-data bekleniyor.");
        var form = await c.Request.ReadFormAsync(c.RequestAborted);
        var file = form.Files["file"] ?? throw GatewayException.Invalid("'file' alanı zorunlu.");
        if (file.Length > cfg.GetValue("Limits:UploadMaxBytes", 10L << 20)) throw new GatewayException(413, "upload_too_large", "Dosya boyut sınırını aşıyor.");
        await using var stream = file.OpenReadStream();
        var img = await ImageSanitizer.SanitizeAsync(stream, cfg.GetValue("Limits:ImageMaxPixels", 20_000_000L), c.RequestAborted);
        var asset = new UploadAsset
        {
            WorkspaceId = caller.WorkspaceId, EnvironmentId = caller.EnvironmentId, MimeType = img.MimeType, Width = img.Width, Height = img.Height,
            ObjectKey = "", ExpiresAt = DateTime.UtcNow.AddMinutes(cfg.GetValue("Limits:UploadTtlMinutes", 15)),
        };
        asset.ObjectKey = $"{caller.WorkspaceId}/{caller.EnvironmentId}/{asset.Id}";
        await storage.PutAsync(asset.ObjectKey, img.Bytes, img.MimeType, c.RequestAborted);
        db.Uploads.Add(asset);
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Json(new { assetId = asset.Id, asset.ExpiresAt, asset.MimeType, asset.Width, asset.Height }, statusCode: 201);
    }

    static async Task<IResult> DeleteUpload(Caller caller, Guid assetId, HttpContext c, GatewayDb db)
    {
        var asset = await db.Uploads.SingleOrDefaultAsync(u => u.WorkspaceId == caller.WorkspaceId && u.EnvironmentId == caller.EnvironmentId
            && u.Id == assetId && u.State == "available") ?? throw GatewayException.NotFound();
        await c.RequestServices.GetRequiredService<IObjectStorage>().DeleteAsync(asset.ObjectKey, CancellationToken.None);
        asset.State = "deleted";
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    static async Task Generate(Caller caller, GenerationBody body, string? idempotencyKey, HttpContext c)
    {
        var svc = c.RequestServices.GetRequiredService<InferenceService>();
        var (prepared, replay) = await svc.PrepareAsync(caller, body, idempotencyKey, c.RequestAborted);
        if (replay is not null)
        {
            c.Response.Headers["Idempotent-Replayed"] = "true";
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync(replay);
            return;
        }
        var requestId = prepared!.Request.Id;
        c.Items["requestId"] = requestId;
        c.Response.Headers["X-Request-Id"] = requestId.ToString();

        if (!body.Stream)
        {
            await c.Response.WriteAsJsonAsync(await svc.RunAsync(prepared, null, c.RequestAborted), JsonDefaults.Options);
            return;
        }

        c.Response.ContentType = "text/event-stream";
        c.Response.Headers.CacheControl = "no-cache";
        c.Response.Headers["X-Accel-Buffering"] = "no";
        c.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var gate = new SemaphoreSlim(1, 1);
        async Task Send(string frame)
        {
            await gate.WaitAsync();
            try
            {
                await c.Response.WriteAsync(frame);
                await c.Response.Body.FlushAsync();
            }
            catch (Exception) when (c.RequestAborted.IsCancellationRequested) { } // client gone; RequestAborted cancels the provider
            finally { gate.Release(); }
        }
        Task Event(string name, object data) => Send($"event: {name}\ndata: {JsonSerializer.Serialize(data, JsonDefaults.Options)}\n\n");

        await Event("started", new { requestId, profileRevisionId = prepared.Request.ProfileRevisionId });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try { while (await timer.WaitForNextTickAsync(stop.Token)) await Send(": ping\n\n"); }
            catch (OperationCanceledException) { }
        });
        var sequence = 0;
        try
        {
            var response = await svc.RunAsync(prepared, d => Event("text.delta", new { requestId, sequence = ++sequence, text = d }), c.RequestAborted);
            await Event("completed", response);
        }
        catch (GatewayException e)
        {
            await Event("error", new { requestId, code = e.Code, message = e.Message, retryable = e.Retryable, partial = sequence > 0 });
        }
        finally
        {
            stop.Cancel();
            await heartbeat;
        }
    }
}

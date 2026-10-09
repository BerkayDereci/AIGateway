using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiGateway.Core;
using AiGateway.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api;

public record RegisterBody(string Email, string Password, string WorkspaceName);
public record LoginBody(string Email, string Password);
public record NameBody(string Name);
public record MemberBody(string Email, string Role);
public record RoleBody(string Role);
public record ProjectPatch(string? Name, bool? Archived);
public record ConnectionBody(string Provider, string Name, string ApiKey);
public record ConnectionPatch(string? Name, string? ApiKey);
public record KeyBody(string Name, DateTime? ExpiresAt);
public record RevisionBody(ProfileConfig Config);
public record BindingBody(Guid RevisionId);
public record LimitBody(string Scope, string ScopeKey, decimal? MonthlyUsd, int? MonthlyRequests, long? MonthlyTokens, int? Concurrency);
public record PriceBody(string Provider, string Model, DateTime EffectiveAt, decimal InputPerMillion, decimal OutputPerMillion,
    decimal? CachedInputPerMillion, int? ImageInputTokens, string Source);

public static partial class AdminEndpoints
{
    static readonly string[] Write = [Roles.Owner, Roles.Admin];

    public static Guid UserId(HttpContext c) => Guid.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Membership check at the command boundary. Non-members get 404 so workspace IDs are not probeable.</summary>
    public static async Task<WorkspaceMember> Require(GatewayDb db, HttpContext c, Guid ws, params string[] roles)
    {
        var uid = UserId(c);
        var m = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == ws && x.UserId == uid)
            ?? throw GatewayException.NotFound();
        if (roles.Length > 0 && !roles.Contains(m.Role)) throw GatewayException.Forbidden();
        return m;
    }

    static void Audit(GatewayDb db, Guid ws, HttpContext c, string action, object target) =>
        db.Audit.Add(new AuditEvent { WorkspaceId = ws, ActorUserId = UserId(c), Action = action, Target = target.ToString()! });

    static string Required(string? v, string field, int max = 200) =>
        string.IsNullOrWhiteSpace(v) || v.Length > max ? throw GatewayException.Invalid($"{field} 1..{max} karakter olmalı.") : v.Trim();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")] private static partial Regex Slug();

    public static async Task<object> Paged<T>(IQueryable<T> q, string? cursor, int? limit, Func<T, Guid> id) where T : class
    {
        var (items, next) = await Page(q, cursor, limit, id);
        return new { items, nextCursor = next };
    }

    /// <summary>Keyset paging on time-ordered UUIDv7 ids; the cursor is opaque base64.</summary>
    static async Task<(List<T> Items, string? Next)> Page<T>(IQueryable<T> q, string? cursor, int? limit, Func<T, Guid> id) where T : class
    {
        var n = Math.Clamp(limit ?? 25, 1, 100);
        if (cursor is not null)
        {
            Guid after;
            try { after = new Guid(Convert.FromBase64String(cursor)); }
            catch (FormatException) { throw GatewayException.Invalid("Geçersiz cursor."); }
            q = q.Where(x => EF.Property<Guid>(x, "Id").CompareTo(after) < 0);
        }
        var items = await q.OrderByDescending(x => EF.Property<Guid>(x, "Id")).Take(n + 1).ToListAsync();
        var next = items.Count > n ? Convert.ToBase64String(id(items[n - 1]).ToByteArray()) : null;
        return (items.Take(n).ToList(), next);
    }

    // CSRF for every state-changing admin route, including login/register (login CSRF).
    public static async ValueTask<object?> Csrf(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) &&
            !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
            throw new GatewayException(400, "invalid_request", "CSRF doğrulaması başarısız. Sayfayı yenileyin.");
        return await next(ctx);
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter(Csrf);
        var auth = api.MapGroup("/auth");

        auth.MapGet("/csrf", (HttpContext c, IAntiforgery af) => new { token = af.GetAndStoreTokens(c).RequestToken });

        auth.MapPost("/register", async (RegisterBody b, GatewayDb db, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var name = Required(b.WorkspaceName, "workspaceName", 100);
            await using var tx = await db.Database.BeginTransactionAsync();
            var user = new AppUser { UserName = b.Email, Email = b.Email };
            var result = await users.CreateAsync(user, b.Password ?? "");
            if (!result.Succeeded)
            {
                var pw = result.Errors.Where(e => e.Code.StartsWith("Password")).Select(e => e.Description).ToList();
                throw GatewayException.Invalid(pw.Count > 0 ? string.Join(" ", pw) : "Bu bilgilerle kayıt oluşturulamadı.");
            }
            var ws = new Workspace { Name = name };
            db.Workspaces.Add(ws);
            db.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = user.Id, Role = Roles.Owner });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.Created("/api/v1/auth/me", new { userId = user.Id, workspaceId = ws.Id });
        }).RequireRateLimiting("auth");

        auth.MapPost("/login", async (LoginBody b, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.FindByEmailAsync(b.Email ?? "");
            var ok = user is not null && (await signIn.PasswordSignInAsync(user, b.Password ?? "", false, lockoutOnFailure: true)).Succeeded;
            return ok ? Results.NoContent() : throw new GatewayException(401, "invalid_credentials", "E-posta veya parola hatalı.");
        }).RequireRateLimiting("auth");

        auth.MapPost("/logout", async (SignInManager<AppUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); });

        auth.MapGet("/me", async (HttpContext c, GatewayDb db) =>
        {
            var uid = UserId(c);
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == uid);
            var workspaces = await (from m in db.Members join w in db.Workspaces on m.WorkspaceId equals w.Id
                where m.UserId == uid orderby w.CreatedAt select new { w.Id, w.Name, w.Plan, m.Role }).ToListAsync();
            return new { userId = uid, user.Email, workspaces };
        }).RequireAuthorization();

        var wsg = api.MapGroup("/workspaces").RequireAuthorization();

        wsg.MapGet("/", async (HttpContext c, GatewayDb db) =>
        {
            var uid = UserId(c);
            return await (from m in db.Members join w in db.Workspaces on m.WorkspaceId equals w.Id
                where m.UserId == uid select new { w.Id, w.Name, w.Plan, m.Role }).ToListAsync();
        });

        wsg.MapPost("/", async (NameBody b, HttpContext c, GatewayDb db) =>
        {
            var ws = new Workspace { Name = Required(b.Name, "name", 100) };
            db.Workspaces.Add(ws);
            db.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = UserId(c), Role = Roles.Owner });
            await db.SaveChangesAsync();
            return Results.Created($"/api/v1/workspaces/{ws.Id}", new { ws.Id, ws.Name, ws.Plan, role = Roles.Owner });
        });

        var w = wsg.MapGroup("/{workspaceId:guid}");

        // ---- Members ----
        w.MapGet("/members", async (Guid workspaceId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            return await (from m in db.Members join u in db.Users on m.UserId equals u.Id
                where m.WorkspaceId == workspaceId select new { userId = u.Id, u.Email, m.Role }).ToListAsync();
        });

        w.MapPost("/members", async (Guid workspaceId, MemberBody b, HttpContext c, GatewayDb db, UserManager<AppUser> users) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            if (!Roles.All.Contains(b.Role)) throw GatewayException.Invalid("Geçersiz rol.");
            var user = await users.FindByEmailAsync(b.Email ?? "") ?? throw new GatewayException(404, "not_found", "Kayıtlı kullanıcı bulunamadı.");
            if (await db.Members.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == user.Id))
                throw new GatewayException(409, "conflict", "Kullanıcı zaten üye.");
            db.Members.Add(new WorkspaceMember { WorkspaceId = workspaceId, UserId = user.Id, Role = b.Role });
            Audit(db, workspaceId, c, "member.add", user.Id);
            await db.SaveChangesAsync();
            return Results.Created("", new { userId = user.Id, user.Email, b.Role });
        });

        w.MapPatch("/members/{userId:guid}", async (Guid workspaceId, Guid userId, RoleBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            if (!Roles.All.Contains(b.Role)) throw GatewayException.Invalid("Geçersiz rol.");
            var m = await db.Members.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.UserId == userId) ?? throw GatewayException.NotFound();
            if (m.Role == Roles.Owner && b.Role != Roles.Owner &&
                await db.Members.CountAsync(x => x.WorkspaceId == workspaceId && x.Role == Roles.Owner) == 1)
                throw GatewayException.Invalid("Son owner'ın rolü değiştirilemez.");
            m.Role = b.Role;
            Audit(db, workspaceId, c, "member.role", $"{userId}:{b.Role}");
            await db.SaveChangesAsync();
            return new { userId, m.Role };
        });

        // ---- Projects & environments ----
        w.MapGet("/projects", async (Guid workspaceId, HttpContext c, GatewayDb db, string? cursor, int? limit) =>
        {
            await Require(db, c, workspaceId);
            return await Paged(db.Projects.AsNoTracking().Where(p => p.WorkspaceId == workspaceId), cursor, limit, p => p.Id);
        });

        w.MapPost("/projects", async (Guid workspaceId, NameBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            var p = new Project { WorkspaceId = workspaceId, Name = Required(b.Name, "name", 100) };
            db.Projects.Add(p);
            foreach (var t in new[] { "development", "production" })
                db.Environments.Add(new GatewayEnvironment { WorkspaceId = workspaceId, ProjectId = p.Id, Type = t });
            Audit(db, workspaceId, c, "project.create", p.Id);
            await db.SaveChangesAsync();
            return Results.Created($"/api/v1/workspaces/{workspaceId}/projects/{p.Id}", p);
        });

        w.MapGet("/projects/{projectId:guid}", async (Guid workspaceId, Guid projectId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            return await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Id == projectId)
                ?? throw GatewayException.NotFound();
        });

        w.MapPatch("/projects/{projectId:guid}", async (Guid workspaceId, Guid projectId, ProjectPatch b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            var p = await db.Projects.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == projectId) ?? throw GatewayException.NotFound();
            if (b.Name is not null) p.Name = Required(b.Name, "name", 100);
            if (b.Archived is { } a) p.ArchivedAt = a ? p.ArchivedAt ?? DateTime.UtcNow : null;
            Audit(db, workspaceId, c, "project.update", p.Id);
            await db.SaveChangesAsync();
            return p;
        });

        w.MapGet("/projects/{projectId:guid}/environments", async (Guid workspaceId, Guid projectId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            return await db.Environments.AsNoTracking().Where(e => e.WorkspaceId == workspaceId && e.ProjectId == projectId)
                .OrderBy(e => e.Type).ToListAsync();
        });

        // ---- Provider connections ----
        static object ConnectionDto(ProviderConnection x) => new { x.Id, x.Provider, x.Name, x.MaskedSuffix, x.Status, x.CreatedAt, x.VerifiedAt };
        static string ValidKey(string? k) => k is { Length: >= 20 and <= 400 } && !k.Any(char.IsWhiteSpace)
            ? k : throw GatewayException.Invalid("API anahtarı biçimi geçersiz.");

        w.MapGet("/provider-connections", async (Guid workspaceId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            return (await db.ProviderConnections.AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.CreatedAt).ToListAsync())
                .Select(ConnectionDto);
        });

        w.MapPost("/provider-connections", async (Guid workspaceId, ConnectionBody b, HttpContext c, GatewayDb db, SecretProtector sp) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            if (b.Provider is not ("openai" or "anthropic")) throw GatewayException.Invalid("provider openai veya anthropic olmalı.");
            var key = ValidKey(b.ApiKey);
            var x = new ProviderConnection
            {
                WorkspaceId = workspaceId, Provider = b.Provider, Name = Required(b.Name, "name", 100),
                Ciphertext = sp.Protect(key), KeyVersion = SecretProtector.KeyVersion, MaskedSuffix = "…" + key[^4..],
            };
            db.ProviderConnections.Add(x);
            Audit(db, workspaceId, c, "connection.create", x.Id);
            await db.SaveChangesAsync();
            return Results.Created("", ConnectionDto(x));
        });

        w.MapPatch("/provider-connections/{connectionId:guid}", async (Guid workspaceId, Guid connectionId, ConnectionPatch b, HttpContext c, GatewayDb db, SecretProtector sp) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            var x = await db.ProviderConnections.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Id == connectionId) ?? throw GatewayException.NotFound();
            if (b.Name is not null) x.Name = Required(b.Name, "name", 100);
            if (b.ApiKey is not null)
            {
                var key = ValidKey(b.ApiKey);
                (x.Ciphertext, x.KeyVersion, x.MaskedSuffix, x.Status, x.VerifiedAt) = (sp.Protect(key), SecretProtector.KeyVersion, "…" + key[^4..], "unverified", null);
            }
            Audit(db, workspaceId, c, "connection.update", x.Id);
            await db.SaveChangesAsync();
            return ConnectionDto(x);
        });

        w.MapDelete("/provider-connections/{connectionId:guid}", async (Guid workspaceId, Guid connectionId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            var x = await db.ProviderConnections.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Id == connectionId) ?? throw GatewayException.NotFound();
            db.ProviderConnections.Remove(x);
            Audit(db, workspaceId, c, "connection.delete", x.Id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        w.MapPost("/provider-connections/{connectionId:guid}/verify", async (Guid workspaceId, Guid connectionId, HttpContext c, GatewayDb db,
            SecretProtector sp, ProviderRegistry providers, CancellationToken ct) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            var x = await db.ProviderConnections.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Id == connectionId, ct) ?? throw GatewayException.NotFound();
            bool ok;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { ok = await providers.VerifyAsync(x.Provider, sp.Unprotect(x.Ciphertext), timeout.Token); }
                catch (Exception e) when (e is ProviderException or HttpRequestException or OperationCanceledException)
                { throw new GatewayException(502, "provider_error", "Sağlayıcıya ulaşılamadı; durum değiştirilmedi.", true); }
            }
            (x.Status, x.VerifiedAt) = ok ? ("verified", DateTime.UtcNow) : ("failed", x.VerifiedAt);
            Audit(db, workspaceId, c, "connection.verify", $"{x.Id}:{x.Status}");
            await db.SaveChangesAsync(CancellationToken.None);
            return ConnectionDto(x);
        });

        // ---- API keys ----
        static object KeyDto(ApiKey k) => new { k.Id, k.Name, k.Prefix, k.CreatedAt, k.ExpiresAt, k.RevokedAt };

        async Task<GatewayEnvironment> Env(GatewayDb db, Guid ws, Guid envId) =>
            await db.Environments.AsNoTracking().SingleOrDefaultAsync(e => e.WorkspaceId == ws && e.Id == envId) ?? throw GatewayException.NotFound();

        w.MapGet("/environments/{environmentId:guid}/keys", async (Guid workspaceId, Guid environmentId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            await Env(db, workspaceId, environmentId);
            return (await db.ApiKeys.AsNoTracking().Where(k => k.WorkspaceId == workspaceId && k.EnvironmentId == environmentId)
                .OrderByDescending(k => k.CreatedAt).Take(100).ToListAsync()).Select(KeyDto);
        });

        w.MapPost("/environments/{environmentId:guid}/keys", async (Guid workspaceId, Guid environmentId, KeyBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            await Env(db, workspaceId, environmentId);
            if (b.ExpiresAt is { } exp && exp <= DateTime.UtcNow) throw GatewayException.Invalid("expiresAt gelecekte olmalı.");
            var secret = ApiKeys.Generate();
            var k = new ApiKey
            {
                WorkspaceId = workspaceId, EnvironmentId = environmentId, Name = Required(b.Name, "name", 100),
                Prefix = ApiKeys.PrefixOf(secret), KeyHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)), ExpiresAt = b.ExpiresAt,
            };
            db.ApiKeys.Add(k);
            Audit(db, workspaceId, c, "key.create", k.Id);
            await db.SaveChangesAsync();
            return Results.Created("", new { k.Id, k.Name, k.Prefix, k.CreatedAt, k.ExpiresAt, key = secret });
        });

        w.MapDelete("/environments/{environmentId:guid}/keys/{keyId:guid}", async (Guid workspaceId, Guid environmentId, Guid keyId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            var k = await db.ApiKeys.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.EnvironmentId == environmentId && x.Id == keyId)
                ?? throw GatewayException.NotFound();
            k.RevokedAt ??= DateTime.UtcNow;
            Audit(db, workspaceId, c, "key.revoke", k.Id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- Profiles ----
        w.MapGet("/profiles", async (Guid workspaceId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            var profiles = await db.Profiles.AsNoTracking().Where(p => p.WorkspaceId == workspaceId).OrderBy(p => p.Name).Take(100).ToListAsync();
            var revisions = await db.ProfileRevisions.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.Number).ToListAsync();
            var bindings = await db.Bindings.AsNoTracking().Where(b => b.WorkspaceId == workspaceId).ToListAsync();
            return profiles.Select(p => new
            {
                p.Id, p.Name, p.CreatedAt,
                revisions = revisions.Where(r => r.ProfileId == p.Id).Select(r => new
                {
                    r.Id, r.Number, r.CreatedAt, config = JsonSerializer.Deserialize<ProfileConfig>(r.ConfigJson, JsonSerializerOptions.Web),
                }),
                bindings = bindings.Where(b => b.ProfileId == p.Id).Select(b => new { b.EnvironmentId, b.RevisionId }),
            });
        });

        w.MapPost("/profiles", async (Guid workspaceId, NameBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            if (!Slug().IsMatch(b.Name ?? "")) throw GatewayException.Invalid("Profil adı küçük harf, rakam ve tire içermeli (1-63).");
            if (await db.Profiles.AnyAsync(p => p.WorkspaceId == workspaceId && p.Name == b.Name)) throw new GatewayException(409, "conflict", "Bu adla profil var.");
            var p = new Profile { WorkspaceId = workspaceId, Name = b.Name! };
            db.Profiles.Add(p);
            Audit(db, workspaceId, c, "profile.create", p.Id);
            await db.SaveChangesAsync();
            return Results.Created("", new { p.Id, p.Name, p.CreatedAt, revisions = Array.Empty<object>(), bindings = Array.Empty<object>() });
        });

        w.MapPost("/profiles/{profileId:guid}/revisions", async (Guid workspaceId, Guid profileId, RevisionBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            var p = await db.Profiles.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == profileId) ?? throw GatewayException.NotFound();
            await ValidateConfig(db, workspaceId, b.Config);
            await using var tx = await db.Database.BeginTransactionAsync();
            var number = (await db.ProfileRevisions.Where(r => r.ProfileId == p.Id).MaxAsync(r => (int?)r.Number) ?? 0) + 1;
            var r = new ProfileRevision { WorkspaceId = workspaceId, ProfileId = p.Id, Number = number, ConfigJson = JsonSerializer.Serialize(b.Config, JsonSerializerOptions.Web) };
            db.ProfileRevisions.Add(r);
            Audit(db, workspaceId, c, "profile.revision", r.Id);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created("", new { r.Id, r.Number, r.CreatedAt, config = b.Config });
        });

        w.MapPut("/environments/{environmentId:guid}/profiles/{profileId:guid}", async (Guid workspaceId, Guid environmentId, Guid profileId, BindingBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            await Env(db, workspaceId, environmentId);
            if (!await db.ProfileRevisions.AnyAsync(r => r.WorkspaceId == workspaceId && r.ProfileId == profileId && r.Id == b.RevisionId))
                throw GatewayException.NotFound();
            var binding = await db.Bindings.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId && x.ProfileId == profileId);
            if (binding is null) db.Bindings.Add(new EnvironmentProfileBinding { WorkspaceId = workspaceId, EnvironmentId = environmentId, ProfileId = profileId, RevisionId = b.RevisionId });
            else binding.RevisionId = b.RevisionId;
            Audit(db, workspaceId, c, "profile.publish", $"{profileId}@{environmentId}:{b.RevisionId}");
            await db.SaveChangesAsync();
            return new { environmentId, profileId, b.RevisionId };
        });

        // ---- Limits ----
        w.MapGet("/limits", async (Guid workspaceId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            var period = BudgetService.PeriodOf(DateTime.UtcNow);
            return new
            {
                period,
                limits = await db.Limits.AsNoTracking().Where(l => l.WorkspaceId == workspaceId).ToListAsync(),
                buckets = await db.Buckets.AsNoTracking().Where(b => b.WorkspaceId == workspaceId && b.Period == period)
                    .OrderBy(b => b.Scope).ThenBy(b => b.ScopeKey).Take(200).ToListAsync(),
                reviewRequired = await db.Reservations.CountAsync(r => r.WorkspaceId == workspaceId && r.State == ReservationState.ReviewRequired),
            };
        });

        w.MapPatch("/limits", async (Guid workspaceId, LimitBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Write);
            var keyOk = b.Scope switch
            {
                Scopes.Workspace => b.ScopeKey == "",
                Scopes.EndUser => b.ScopeKey == "*",
                Scopes.Project => Guid.TryParse(b.ScopeKey, out var pid) && await db.Projects.AnyAsync(p => p.WorkspaceId == workspaceId && p.Id == pid),
                _ => false,
            };
            if (!keyOk) throw GatewayException.Invalid("Geçersiz scope/scopeKey.");
            if (b.MonthlyUsd < 0 || b.MonthlyRequests < 0 || b.MonthlyTokens < 0 || b.Concurrency < 1) throw GatewayException.Invalid("Limitler negatif olamaz.");
            var l = await db.Limits.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Scope == b.Scope && x.ScopeKey == b.ScopeKey);
            if (l is null) db.Limits.Add(l = new Limit { WorkspaceId = workspaceId, Scope = b.Scope, ScopeKey = b.ScopeKey });
            (l.MonthlyUsd, l.MonthlyRequests, l.MonthlyTokens, l.Concurrency) = (b.MonthlyUsd, b.MonthlyRequests, b.MonthlyTokens, b.Concurrency);
            Audit(db, workspaceId, c, "limits.update", $"{b.Scope}:{b.ScopeKey}");
            await db.SaveChangesAsync();
            return l;
        });

        // ---- Usage ----
        w.MapGet("/usage", async (Guid workspaceId, HttpContext c, GatewayDb db, Guid? projectId, DateTime? from, DateTime? to, string? cursor, int? limit) =>
        {
            await Require(db, c, workspaceId);
            var q = db.Requests.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
            if (projectId is { } pid) q = q.Where(r => r.ProjectId == pid);
            if (from is { } f) q = q.Where(r => r.CreatedAt >= f.ToUniversalTime());
            if (to is { } t) q = q.Where(r => r.CreatedAt < t.ToUniversalTime());

            var (rows, nextCursor) = await Page(q, cursor, limit, r => r.Id);
            var ids = rows.Select(r => r.Id).ToList();
            var attempts = await db.Attempts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && ids.Contains(a.RequestId)).ToListAsync();

            var attemptsInRange = db.Attempts.Where(a => a.WorkspaceId == workspaceId && q.Select(r => r.Id).Contains(a.RequestId));
            var total = await q.CountAsync();
            var durations = q.Where(r => r.DurationMs != null).OrderBy(r => r.DurationMs);
            var durationCount = await durations.CountAsync();
            return new
            {
                items = rows.Select(r =>
                {
                    var a = attempts.Where(x => x.RequestId == r.Id).ToList();
                    var known = a.Count > 0 && a.All(x => x.CostState == "estimated");
                    return new
                    {
                        requestId = r.Id, r.CreatedAt, r.Status, r.Feature, r.Provider, r.Model, r.DurationMs, r.ErrorCode, r.ProjectId, r.EndUserRef,
                        attempts = a.Count,
                        inputTokens = a.All(x => x.UsageState == "reported") ? a.Sum(x => x.InputTokens) : null,
                        outputTokens = a.All(x => x.UsageState == "reported") ? a.Sum(x => x.OutputTokens) : null,
                        estimatedCost = known ? a.Sum(x => x.EstimatedCost) : null,
                        costState = a.Count == 0 ? "none" : known ? "estimated" : "unknown",
                    };
                }),
                nextCursor,
                summary = new
                {
                    requests = total,
                    errors = await q.CountAsync(r => r.Status != RequestStatus.Completed && r.Status != RequestStatus.InProgress),
                    estimatedUsd = await attemptsInRange.Where(a => a.CostState == "estimated").SumAsync(a => a.EstimatedCost ?? 0),
                    unknownCostRequests = await q.CountAsync(r => db.Attempts.Any(a => a.WorkspaceId == workspaceId && a.RequestId == r.Id && a.CostState == "unknown")),
                    p95LatencyMs = durationCount == 0 ? (int?)null
                        : await durations.Skip((int)Math.Ceiling(durationCount * 0.95) - 1).Select(r => r.DurationMs).FirstAsync(),
                },
            };
        });

        w.MapGet("/requests/{requestId:guid}", async (Guid workspaceId, Guid requestId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            var r = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == requestId) ?? throw GatewayException.NotFound();
            var attempts = await db.Attempts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.RequestId == requestId).OrderBy(a => a.AttemptNumber).ToListAsync();
            var reservation = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RequestId == requestId);
            return new
            {
                requestId = r.Id, r.Status, r.CreatedAt, r.CompletedAt, r.DurationMs, r.Feature, r.Provider, r.Model, r.ErrorCode, r.EndUserRef,
                r.ProjectId, r.EnvironmentId, r.ProfileRevisionId, r.Stream,
                reservation = reservation is null ? null : new { reservation.Amount, reservation.State },
                attempts = attempts.Select(a => new
                {
                    a.AttemptNumber, a.Provider, a.Model, a.Status, a.ProviderRequestId, a.InputTokens, a.OutputTokens, a.CachedInputTokens,
                    a.UsageState, a.EstimatedCost, a.CostState, a.PriceVersionId, a.ErrorCode, a.StartedAt, a.EndedAt, a.TtftMs,
                }),
            };
        });

        // ---- Models & pricing ----
        w.MapGet("/models", async (Guid workspaceId, HttpContext c, GatewayDb db, ProviderRegistry providers) =>
        {
            await Require(db, c, workspaceId);
            var enabled = providers.Providers.ToList();
            var priced = await db.Prices.Where(p => p.WorkspaceId == workspaceId).Select(p => p.Provider + "/" + p.Model).Distinct().ToListAsync();
            return (await db.Models.AsNoTracking().Where(m => enabled.Contains(m.Provider)).OrderBy(m => m.Provider).ThenBy(m => m.Model).ToListAsync())
                .Select(m => new { m.Provider, m.Model, m.Text, m.Image, m.Streaming, m.JsonSchema, m.Source, m.VerifiedAt, priced = priced.Contains(m.Provider + "/" + m.Model) });
        });

        w.MapGet("/pricing", async (Guid workspaceId, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId);
            return await db.Prices.AsNoTracking().Where(p => p.WorkspaceId == workspaceId).OrderByDescending(p => p.EffectiveAt).Take(100).ToListAsync();
        });

        w.MapPost("/pricing", async (Guid workspaceId, PriceBody b, HttpContext c, GatewayDb db) =>
        {
            await Require(db, c, workspaceId, Roles.Owner);
            if (!await db.Models.AnyAsync(m => m.Provider == b.Provider && m.Model == b.Model)) throw new GatewayException(403, "model_not_allowed", "Model doğrulanmış listede değil.");
            if (b.InputPerMillion < 0 || b.OutputPerMillion < 0 || b.CachedInputPerMillion < 0 || b.ImageInputTokens < 0)
                throw GatewayException.Invalid("Fiyatlar negatif olamaz.");
            var p = new PriceVersion
            {
                WorkspaceId = workspaceId, Provider = b.Provider, Model = b.Model, EffectiveAt = b.EffectiveAt.ToUniversalTime(),
                InputPerMillion = b.InputPerMillion, OutputPerMillion = b.OutputPerMillion, CachedInputPerMillion = b.CachedInputPerMillion,
                ImageInputTokens = b.ImageInputTokens, Source = Required(b.Source, "source", 500), CreatedBy = UserId(c),
            };
            db.Prices.Add(p);
            Audit(db, workspaceId, c, "pricing.create", p.Id);
            await db.SaveChangesAsync();
            return Results.Created("", p);
        });

        w.MapGet("/audit", async (Guid workspaceId, HttpContext c, GatewayDb db, string? cursor, int? limit) =>
        {
            await Require(db, c, workspaceId, Write);
            return await Paged(db.Audit.AsNoTracking().Where(a => a.WorkspaceId == workspaceId), cursor, limit, a => a.Id);
        });
    }

    static async Task ValidateConfig(GatewayDb db, Guid ws, ProfileConfig? cfg)
    {
        if (cfg?.Primary is null) throw GatewayException.Invalid("config.primary zorunlu.");
        if (cfg.OutputTokenLimit is < 1 or > 64000) throw GatewayException.Invalid("outputTokenLimit 1..64000 olmalı.");
        if (cfg.TimeoutSeconds is < 5 or > 600) throw GatewayException.Invalid("timeoutSeconds 5..600 olmalı.");
        if (cfg.AutomaticRetryCount is < 0 or > 1) throw GatewayException.Invalid("automaticRetryCount 0 veya 1 olmalı.");
        if (cfg.SchemaMode is not ("native" or "validation")) throw GatewayException.Invalid("schemaMode native veya validation olmalı.");
        if ((cfg.Fallbacks ?? []).Count > 3) throw GatewayException.Invalid("En fazla 3 fallback.");
        if ((cfg.RequiredCapabilities ?? []).Any(x => x is not ("text" or "image" or "streaming" or "jsonSchema")))
            throw GatewayException.Invalid("Geçersiz yetenek.");
        foreach (var m in new[] { cfg.Primary }.Concat(cfg.Fallbacks ?? []))
        {
            var cap = await db.Models.AsNoTracking().SingleOrDefaultAsync(x => x.Provider == m.Provider && x.Model == m.Model)
                ?? throw new GatewayException(403, "model_not_allowed", $"Model doğrulanmış listede değil: {m.Provider}/{m.Model}");
            var missing = (cfg.RequiredCapabilities ?? []).Where(r => !(r switch
                { "text" => cap.Text, "image" => cap.Image, "streaming" => cap.Streaming, _ => cap.JsonSchema })).ToList();
            if (missing.Count > 0) throw new GatewayException(400, "unsupported_capability", $"{m.Model}: {string.Join(", ", missing)} desteklenmiyor.");
            if (m.Provider != "demo" && !await db.ProviderConnections.AnyAsync(x => x.WorkspaceId == ws && x.Id == m.ConnectionId && x.Provider == m.Provider))
                throw GatewayException.Invalid($"{m.Model} için bu workspace'te eşleşen sağlayıcı bağlantısı yok.");
        }
    }
}

public static class ApiKeys
{
    public static string Generate() => "pgw_" + Base64Url(RandomNumberGenerator.GetBytes(32)); // 256-bit secret
    public static string PrefixOf(string key) => key[..16];
    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

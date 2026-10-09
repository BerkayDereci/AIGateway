using AiGateway.Core;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Infrastructure;

/// <summary>
/// Atomic multi-scope reservation on PostgreSQL row locks. Buckets are locked in (Scope, ScopeKey) order so
/// concurrent requests never deadlock; no network I/O happens while the transaction is open.
/// </summary>
public class BudgetService(GatewayDb db)
{
    public static string PeriodOf(DateTime utc) => utc.ToString("yyyy-MM");

    static List<(string Scope, string Key)> ScopesOf(Guid projectId, string? endUser) =>
        [(Scopes.Workspace, ""), (Scopes.Project, projectId.ToString()), .. endUser is null ? [] : new[] { (Scopes.EndUser, endUser) }];

    public async Task ReserveAsync(Guid ws, Guid projectId, string? endUser, Guid requestId, decimal? amount,
        long estimatedTokens, int leaseSeconds, bool pricingRequired)
    {
        var now = DateTime.UtcNow;
        var period = PeriodOf(now);
        var scopes = ScopesOf(projectId, endUser);
        var limits = await db.Limits.AsNoTracking().Where(l => l.WorkspaceId == ws).ToListAsync();
        Limit? LimitFor((string Scope, string Key) s) =>
            limits.FirstOrDefault(l => l.Scope == s.Scope && l.ScopeKey == s.Key)
            ?? (s.Scope == Scopes.EndUser ? limits.FirstOrDefault(l => l.Scope == Scopes.EndUser && l.ScopeKey == "*") : null);

        if (amount is null && pricingRequired && scopes.Any(s => LimitFor(s)?.MonthlyUsd is not null))
            throw new GatewayException(503, "pricing_unavailable", "Bütçe sınırı var ancak bu model için doğrulanmış fiyat yok.");

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var (scope, key) in scopes)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Buckets" ("WorkspaceId","Scope","ScopeKey","Period","Reserved","Settled","Unknown","Requests","Tokens")
                VALUES ({ws},{scope},{key},{period},0,0,0,0,0) ON CONFLICT DO NOTHING
                """);
        var keys = scopes.Select(s => s.Key).ToArray();
        var buckets = await db.Buckets.FromSql($"""
            SELECT * FROM "Buckets" WHERE "WorkspaceId" = {ws} AND "Period" = {period} AND "ScopeKey" = ANY({keys})
            ORDER BY "Scope", "ScopeKey" FOR UPDATE
            """).ToListAsync();
        buckets = buckets.Where(b => scopes.Contains((b.Scope, b.ScopeKey))).ToList(); // ScopeKey alone may collide across scopes

        foreach (var b in buckets)
        {
            var l = LimitFor((b.Scope, b.ScopeKey));
            if (l is null) continue;
            if (l.MonthlyUsd is { } usd && b.Settled + b.Reserved + b.Unknown + (amount ?? 0) > usd)
                throw new GatewayException(429, "budget_exceeded", $"{b.Scope} aylık USD bütçesi aşılır.");
            if (l.MonthlyTokens is { } tok && b.Tokens + estimatedTokens > tok)
                throw new GatewayException(429, "budget_exceeded", $"{b.Scope} aylık token sınırı aşılır.");
            if (l.MonthlyRequests is { } req && b.Requests + 1 > req)
                throw new GatewayException(429, "rate_limit_exceeded", $"{b.Scope} aylık istek sınırı doldu.");
            if (l.Concurrency is { } conc)
            {
                var active = db.Reservations.Where(r => r.WorkspaceId == ws && r.State == ReservationState.Active && r.LeaseExpiresAt > now);
                active = b.Scope switch
                {
                    Scopes.Project => active.Where(r => r.ProjectId == projectId),
                    Scopes.EndUser => active.Where(r => r.EndUserRef == endUser),
                    _ => active,
                };
                if (await active.CountAsync() >= conc)
                    throw new GatewayException(429, "concurrency_limit_exceeded", $"{b.Scope} eşzamanlılık sınırı dolu.", true, TimeSpan.FromSeconds(1));
            }
        }
        foreach (var b in buckets) { b.Reserved += amount ?? 0; b.Requests++; }
        db.Reservations.Add(new Reservation
        {
            RequestId = requestId, WorkspaceId = ws, ProjectId = projectId, EndUserRef = endUser, Period = period,
            Amount = amount ?? 0, LeaseExpiresAt = now.AddSeconds(leaseSeconds),
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Idempotent: only an active reservation transitions. knownCost is added to Settled; when the outcome is
    /// unknown the full reserved amount stays as liability (never released by TTL).
    /// </summary>
    public async Task SettleAsync(Guid requestId, string outcome, decimal knownCost, long tokens)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await db.Reservations.FromSql($"""SELECT * FROM "Reservations" WHERE "RequestId" = {requestId} FOR UPDATE""").SingleOrDefaultAsync();
        if (r is null || r.State != ReservationState.Active) return;
        var scopes = ScopesOf(r.ProjectId, r.EndUserRef);
        var keys = scopes.Select(s => s.Key).ToArray();
        var buckets = await db.Buckets.FromSql($"""
            SELECT * FROM "Buckets" WHERE "WorkspaceId" = {r.WorkspaceId} AND "Period" = {r.Period} AND "ScopeKey" = ANY({keys})
            ORDER BY "Scope", "ScopeKey" FOR UPDATE
            """).ToListAsync();
        foreach (var b in buckets.Where(b => scopes.Contains((b.Scope, b.ScopeKey))))
        {
            b.Reserved -= r.Amount;
            if (outcome == ReservationState.Released) { b.Requests--; continue; }
            b.Settled += knownCost;
            b.Tokens += tokens;
            if (outcome == ReservationState.Unknown) b.Unknown += r.Amount;
        }
        r.State = outcome;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
    }
}

using AiGateway.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiGateway.Infrastructure;

/// <summary>
/// Recovers state after crashes: stale in-flight requests become unknown liability (never auto-released),
/// unknown reservations are flagged for review, and expired or orphaned uploads are deleted.
/// Runs across all tenants but each update is keyed by the row's own WorkspaceId.
/// </summary>
public class Reconciler(IServiceScopeFactory scopes, ILogger<Reconciler> log) : BackgroundService
{
    public static async Task RunOnceAsync(GatewayDb db, BudgetService budget, IObjectStorage storage, DateTime now)
    {
        var stale = await db.Requests.AsNoTracking()
            .Where(r => r.Status == RequestStatus.InProgress && r.CreatedAt < now.AddMinutes(-15)).ToListAsync();
        foreach (var r in stale)
        {
            var dispatched = await db.Attempts.Where(a => a.WorkspaceId == r.WorkspaceId && a.RequestId == r.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "unknown"));
            await budget.SettleAsync(r.Id, dispatched > 0 ? ReservationState.Unknown : ReservationState.Released, 0, 0);
            await db.Requests.Where(x => x.WorkspaceId == r.WorkspaceId && x.Id == r.Id && x.Status == RequestStatus.InProgress)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, dispatched > 0 ? RequestStatus.Unknown : RequestStatus.Failed)
                    .SetProperty(x => x.ErrorCode, "reconciled_after_crash").SetProperty(x => x.CompletedAt, now));
        }
        // No provider usage-reconciliation API is integrated, so unknown liability needs human review.
        await db.Reservations.Where(r => r.State == ReservationState.Unknown)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, ReservationState.ReviewRequired));

        var garbage = await db.Uploads.AsNoTracking().Where(u =>
            (u.State == "available" && u.ExpiresAt < now) || (u.State == "claimed" && u.CreatedAt < now.AddHours(-1))).OrderBy(u => u.Id).Take(500).ToListAsync();
        foreach (var u in garbage)
        {
            await storage.DeleteAsync(u.ObjectKey, CancellationToken.None);
            await db.Uploads.Where(x => x.WorkspaceId == u.WorkspaceId && x.Id == u.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "deleted"));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                await RunOnceAsync(sp.GetRequiredService<GatewayDb>(), sp.GetRequiredService<BudgetService>(), sp.GetRequiredService<IObjectStorage>(), DateTime.UtcNow);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Reconciliation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

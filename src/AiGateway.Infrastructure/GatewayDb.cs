using System.Linq.Expressions;
using AiGateway.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Infrastructure;

public class AppUser : IdentityUser<Guid>;

public class GatewayDb(DbContextOptions<GatewayDb> options) : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> Members => Set<WorkspaceMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<GatewayEnvironment> Environments => Set<GatewayEnvironment>();
    public DbSet<ProviderConnection> ProviderConnections => Set<ProviderConnection>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileRevision> ProfileRevisions => Set<ProfileRevision>();
    public DbSet<EnvironmentProfileBinding> Bindings => Set<EnvironmentProfileBinding>();
    public DbSet<ModelCapability> Models => Set<ModelCapability>();
    public DbSet<PriceVersion> Prices => Set<PriceVersion>();
    public DbSet<GenerationRequest> Requests => Set<GenerationRequest>();
    public DbSet<ProviderAttempt> Attempts => Set<ProviderAttempt>();
    public DbSet<Limit> Limits => Set<Limit>();
    public DbSet<BudgetBucket> Buckets => Set<BudgetBucket>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<UploadAsset> Uploads => Set<UploadAsset>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
        b.Properties<decimal>().HavePrecision(20, 10);

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<WorkspaceMember>().HasKey(x => new { x.WorkspaceId, x.UserId });
        b.Entity<WorkspaceMember>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId);
        b.Entity<WorkspaceMember>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<Project>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId);
        b.Entity<ProviderConnection>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId);
        b.Entity<Profile>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId);
        b.Entity<Profile>().HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
        b.Entity<PriceVersion>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId);
        b.Entity<PriceVersion>().HasIndex(x => new { x.WorkspaceId, x.Provider, x.Model, x.EffectiveAt });
        b.Entity<AuditEvent>().HasIndex(x => new { x.WorkspaceId, x.At });

        // Composite (WorkspaceId, Id) FKs make cross-workspace links impossible at the database level.
        Tenant<GatewayEnvironment, Project>(b, x => new { x.WorkspaceId, x.ProjectId });
        Tenant<ApiKey, GatewayEnvironment>(b, x => new { x.WorkspaceId, x.EnvironmentId });
        Tenant<ProfileRevision, Profile>(b, x => new { x.WorkspaceId, x.ProfileId });
        Tenant<EnvironmentProfileBinding, GatewayEnvironment>(b, x => new { x.WorkspaceId, x.EnvironmentId });
        Tenant<EnvironmentProfileBinding, Profile>(b, x => new { x.WorkspaceId, x.ProfileId });
        Tenant<EnvironmentProfileBinding, ProfileRevision>(b, x => new { x.WorkspaceId, x.RevisionId });
        Tenant<GenerationRequest, GatewayEnvironment>(b, x => new { x.WorkspaceId, x.EnvironmentId });
        Tenant<GenerationRequest, Project>(b, x => new { x.WorkspaceId, x.ProjectId });
        Tenant<ProviderAttempt, GenerationRequest>(b, x => new { x.WorkspaceId, x.RequestId });
        Tenant<UploadAsset, GatewayEnvironment>(b, x => new { x.WorkspaceId, x.EnvironmentId });

        b.Entity<ProfileRevision>().HasIndex(x => new { x.ProfileId, x.Number }).IsUnique();
        b.Entity<EnvironmentProfileBinding>().HasKey(x => new { x.EnvironmentId, x.ProfileId });
        b.Entity<ApiKey>().HasIndex(x => x.Prefix).IsUnique();
        b.Entity<ModelCapability>().HasKey(x => new { x.Provider, x.Model });
        b.Entity<GenerationRequest>().HasIndex(x => new { x.WorkspaceId, x.EnvironmentId, x.ApiKeyId, x.IdempotencyKey })
            .IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        b.Entity<GenerationRequest>().HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        b.Entity<ProviderAttempt>().HasIndex(x => new { x.RequestId, x.AttemptNumber }).IsUnique();
        b.Entity<Limit>().HasKey(x => new { x.WorkspaceId, x.Scope, x.ScopeKey });
        b.Entity<Limit>().HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId);
        b.Entity<BudgetBucket>().HasKey(x => new { x.WorkspaceId, x.Scope, x.ScopeKey, x.Period });
        b.Entity<Reservation>().HasKey(x => x.RequestId);
        b.Entity<Reservation>().HasOne<GenerationRequest>().WithOne()
            .HasForeignKey<Reservation>(x => new { x.WorkspaceId, x.RequestId }).HasPrincipalKey<GenerationRequest>(x => new { x.WorkspaceId, x.Id });
        b.Entity<UploadAsset>().HasIndex(x => new { x.State, x.ExpiresAt });
        b.Entity<ModelCapability>().HasData(VerifiedModels);
    }

    // Verified 2026-10-09 against official docs; see docs/provider-compatibility.md. Never add unverified IDs here.
    static readonly DateTime Verified = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    static ModelCapability Full(string provider, string model, string source) => new()
    {
        Provider = provider, Model = model, Text = true, Image = true, Streaming = true, JsonSchema = true, Source = source, VerifiedAt = Verified,
    };
    static readonly ModelCapability[] VerifiedModels =
    [
        .. new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6-luna" }.Select(m => Full("openai", m, $"https://developers.openai.com/api/docs/models/{m}")),
        .. new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" }
            .Select(m => Full("anthropic", m, "https://platform.claude.com/docs/en/build-with-claude/structured-outputs")),
        Full("demo", "demo-model", "local demo provider (Development/Testing only)"),
    ];

    static void Tenant<TDep, TPrin>(ModelBuilder b, Expression<Func<TDep, object?>> fk) where TDep : class where TPrin : class =>
        b.Entity<TDep>().HasOne<TPrin>().WithMany().HasForeignKey(fk)
            .HasPrincipalKey("WorkspaceId", "Id").OnDelete(DeleteBehavior.Restrict);
}

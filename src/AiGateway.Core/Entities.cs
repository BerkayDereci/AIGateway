namespace AiGateway.Core;

// Tenant-owned entities carry WorkspaceId; composite FKs (WorkspaceId, X) are configured in GatewayDb.

public static class Roles
{
    public const string Owner = "owner", Admin = "admin", Viewer = "viewer";
    public static readonly string[] All = [Owner, Admin, Viewer];
}

public class Workspace
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string Plan { get; set; } = "Pilot"; // manual entitlement, no checkout in V1
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public required string Role { get; set; }
}

public class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
}

public class GatewayEnvironment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public required string Type { get; set; } // development | production
}

public class ProviderConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public required string Provider { get; set; }
    public required string Name { get; set; }
    public required string Ciphertext { get; set; }
    public required string KeyVersion { get; set; }
    public required string MaskedSuffix { get; set; }
    public string Status { get; set; } = "unverified"; // unverified | verified | failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
}

public class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required byte[] KeyHash { get; set; }
    public string Permissions { get; set; } = "inference";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class Profile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ProfileRevision
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProfileId { get; set; }
    public int Number { get; set; }
    public required string ConfigJson { get; set; } // immutable ProfileConfig
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class EnvironmentProfileBinding
{
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid RevisionId { get; set; }
}

public class ModelCapability
{
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public bool Text { get; set; }
    public bool Image { get; set; }
    public bool Streaming { get; set; }
    public bool JsonSchema { get; set; }
    public required string Source { get; set; }
    public DateTime VerifiedAt { get; set; }
}

public class PriceVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public DateTime EffectiveAt { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public int? ImageInputTokens { get; set; } // conservative per-image token estimate; null = unknown tariff
    public required string Source { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class RequestStatus
{
    public const string InProgress = "in_progress", Completed = "completed", Failed = "failed",
        Cancelled = "cancelled", Unknown = "unknown";
}

public class GenerationRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid? ApiKeyId { get; set; }
    public Guid? UserId { get; set; }
    public string? EndUserRef { get; set; }
    public Guid ProfileRevisionId { get; set; }
    public string? Feature { get; set; }
    public bool Stream { get; set; }
    public string? IdempotencyKey { get; set; }
    public byte[]? RequestHash { get; set; }
    public string Status { get; set; } = RequestStatus.InProgress;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ErrorCode { get; set; }
    public string? ResponseJson { get; set; } // non-stream completed response kept only for idempotent replay
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
}

// One row per provider call; doubles as the append-only usage record.
public class ProviderAttempt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid RequestId { get; set; }
    public int AttemptNumber { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string Status { get; set; } = "started"; // started | succeeded | failed | cancelled | unknown
    public string? ProviderRequestId { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public string UsageState { get; set; } = "unknown"; // reported | unknown
    public decimal? EstimatedCost { get; set; }
    public string CostState { get; set; } = "unknown"; // estimated | unknown
    public Guid? PriceVersionId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public int? TtftMs { get; set; }
}

public static class Scopes
{
    public const string Workspace = "workspace", Project = "project", EndUser = "endUser";
}

public class Limit
{
    public Guid WorkspaceId { get; set; }
    public required string Scope { get; set; }
    public required string ScopeKey { get; set; } // "" workspace, projectId, "*" = each end user
    public decimal? MonthlyUsd { get; set; }
    public int? MonthlyRequests { get; set; }
    public long? MonthlyTokens { get; set; }
    public int? Concurrency { get; set; }
}

public class BudgetBucket
{
    public Guid WorkspaceId { get; set; }
    public required string Scope { get; set; }
    public required string ScopeKey { get; set; }
    public required string Period { get; set; } // UTC yyyy-MM
    public decimal Reserved { get; set; }
    public decimal Settled { get; set; }
    public decimal Unknown { get; set; } // liability with no trustworthy amount (timeouts, missing usage)
    public int Requests { get; set; }
    public long Tokens { get; set; }
}

public static class ReservationState
{
    public const string Active = "active", Settled = "settled", Released = "released", Unknown = "unknown",
        ReviewRequired = "review_required";
}

public class Reservation
{
    public Guid RequestId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string? EndUserRef { get; set; }
    public required string Period { get; set; }
    public decimal Amount { get; set; }
    public string State { get; set; } = ReservationState.Active;
    public DateTime LeaseExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UploadAsset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string ObjectKey { get; set; }
    public required string MimeType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string State { get; set; } = "available"; // available | claimed | deleted
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

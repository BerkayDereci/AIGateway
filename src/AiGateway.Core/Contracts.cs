using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AiGateway.Core;

public static class JsonDefaults
{
    /// <summary>Web defaults; non-ASCII text (Turkish) stays readable while HTML-sensitive characters remain escaped.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
}

public class GatewayException(int status, string code, string message, bool retryable = false, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static GatewayException NotFound() => new(404, "not_found", "Kaynak bulunamadı.");
    public static GatewayException Invalid(string msg) => new(400, "invalid_request", msg);
    public static GatewayException Forbidden() => new(403, "forbidden", "Bu işlem için yetkiniz yok.");
}

// ---- Public generation API (specs/API.md) ----
public record ContentBlock(string Type, string? Text = null, Guid? AssetId = null);
public record MessageDto(string Role, List<ContentBlock> Content);
public record OutputSpec(string Format, string? Name = null, JsonElement? Schema = null);
public record GenerationBody(
    string Profile, List<MessageDto> Messages, OutputSpec? Output = null, bool Stream = false,
    string? EndUserRef = null, Dictionary<string, string>? Metadata = null, int? MaxOutputTokens = null);

public record OutputResult(string? Text, JsonElement? Json);
public record UsageDto(int? InputTokens, int? OutputTokens, int? CachedInputTokens, string State);
public record CostDto(string Currency, decimal? EstimatedAmount, string State, Guid? PriceVersionId);
public record GenerationResponse(
    Guid RequestId, string Status, Guid ProfileRevisionId, string Provider, string Model,
    OutputResult Output, UsageDto Usage, CostDto Cost, string? FinishReason);

// ---- Profiles ----
public record ModelRef(string Provider, string Model, Guid? ConnectionId);
public record ProfileConfig(
    List<string> RequiredCapabilities, ModelRef Primary, List<ModelRef> Fallbacks,
    int OutputTokenLimit = 1024, int TimeoutSeconds = 120, string SchemaMode = "native",
    bool PricingRequiredForBudgetEnforcement = true, int AutomaticRetryCount = 0);

// ---- Provider contract ----
public record ProviderPart(string? Text, byte[]? Image = null, string? MimeType = null);
public record ProviderMessage(string Role, List<ProviderPart> Parts);
public record ProviderRequest(string Model, IReadOnlyList<ProviderMessage> Messages, int MaxOutputTokens,
    string? SchemaName, JsonElement? Schema);
public record ProviderUsage(int? InputTokens, int? OutputTokens, int? CachedInputTokens)
{
    public bool Reported => InputTokens is not null && OutputTokens is not null;
}
public record ProviderResult(string Text, string FinishReason, ProviderUsage Usage, string? ProviderRequestId);
public record ProviderStreamEvent(string? Delta, ProviderResult? Final);

/// <summary>Kind: rejected (provider refused before generating, safe to retry/fallback), failed, timeout (ambiguous, may be billed).</summary>
public class ProviderException(string kind, int? httpStatus, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public string Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool RateLimited => HttpStatus == 429;
}

public interface IProviderAdapter
{
    string Provider { get; }
    Task<ProviderResult> GenerateAsync(ProviderRequest request, string apiKey, CancellationToken ct);
    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, string apiKey, CancellationToken ct);
}

public static class Pricing
{
    public static decimal Cost(PriceVersion p, int input, int output, int cached) =>
        ((input - cached) * p.InputPerMillion + cached * (p.CachedInputPerMillion ?? p.InputPerMillion)
         + output * p.OutputPerMillion) / 1_000_000m;

    // shortcut: chars/3 over-estimates tokens for Latin text; swap for a provider tokenizer if estimates prove too loose.
    public static int EstimateTextTokens(int chars) => chars / 3 + 16;
}

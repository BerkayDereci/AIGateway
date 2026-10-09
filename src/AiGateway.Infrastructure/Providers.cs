using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core;

namespace AiGateway.Infrastructure;

/// <summary>Shared HTTP plumbing. No SDK retries: the gateway's RoutingService is the single retry layer.</summary>
public abstract class HttpProviderAdapter(HttpClient http) : IProviderAdapter
{
    public abstract string Provider { get; }
    protected abstract HttpRequestMessage Build(ProviderRequest r, string apiKey, bool stream);
    protected abstract ProviderResult Parse(JsonElement body);
    /// <summary>Returns a text delta, a final result, or neither for one SSE event.</summary>
    protected abstract ProviderStreamEvent? ParseEvent(string type, JsonElement data);

    public async Task<ProviderResult> GenerateAsync(ProviderRequest r, string apiKey, CancellationToken ct)
    {
        using var res = await Send(Build(r, apiKey, false), ct);
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        return Parse((await JsonDocument.ParseAsync(s, cancellationToken: ct)).RootElement);
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest r, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        using var res = await Send(Build(r, apiKey, true), ct);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        string? type = null;
        var data = new StringBuilder();
        // SSE frames end at a blank line; StreamReader handles UTF-8 split across network chunks.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event:")) type = line[6..].Trim();
            else if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
            else if (line.Length == 0 && data.Length > 0)
            {
                var json = JsonDocument.Parse(data.ToString()).RootElement;
                type ??= json.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (ParseEvent(type ?? "", json) is { } ev)
                {
                    yield return ev;
                    if (ev.Final is not null) yield break;
                }
                type = null; data.Clear();
            }
        }
        throw new ProviderException("ambiguous", null, "Sağlayıcı akışı tamamlanmadan kapandı.");
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) when (e.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        { throw new ProviderException("rejected", null, "Sağlayıcıya bağlanılamadı."); }
        catch (HttpRequestException) { throw new ProviderException("ambiguous", null, "Sağlayıcı bağlantısı kesildi."); }
        if (res.IsSuccessStatusCode) return res;
        var status = (int)res.StatusCode;
        var retryAfter = res.Headers.RetryAfter?.Delta;
        res.Dispose(); // raw provider body is never surfaced
        throw status switch
        {
            429 or 503 or 529 => new ProviderException("rejected", status, "Sağlayıcı isteği şu an kabul etmiyor.", retryAfter),
            _ => new ProviderException("failed", status, $"Sağlayıcı HTTP {status} döndü."),
        };
    }

    protected static string DataUrl(ProviderPart p) => $"data:{p.MimeType};base64,{Convert.ToBase64String(p.Image!)}";
    protected static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    protected static HttpRequestMessage Post(string path, JsonNode body) =>
        new(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>OpenAI Responses API (POST /v1/responses). See docs/provider-compatibility.md.</summary>
public class OpenAiAdapter(HttpClient http) : HttpProviderAdapter(http)
{
    public const string BaseUrl = "https://api.openai.com/";
    public override string Provider => "openai";

    protected override HttpRequestMessage Build(ProviderRequest r, string apiKey, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = r.Model,
            ["max_output_tokens"] = r.MaxOutputTokens,
            ["store"] = false,
            ["stream"] = stream,
            ["input"] = new JsonArray(r.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray(m.Parts.Select(p => (JsonNode)(p.Image is not null
                    ? new JsonObject { ["type"] = "input_image", ["image_url"] = DataUrl(p) }
                    : new JsonObject { ["type"] = m.Role == "assistant" ? "output_text" : "input_text", ["text"] = p.Text }))
                    .ToArray()),
            }).ToArray()),
        };
        if (r.Schema is { } schema)
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema", ["name"] = r.SchemaName ?? "output", ["strict"] = true,
                    ["schema"] = JsonNode.Parse(schema.GetRawText()),
                }
            };
        var req = Post("v1/responses", body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }

    protected override ProviderResult Parse(JsonElement b)
    {
        var text = new StringBuilder();
        if (b.TryGetProperty("output", out var output))
            foreach (var item in output.EnumerateArray())
                if (item.TryGetProperty("content", out var content))
                    foreach (var c in content.EnumerateArray())
                        if (c.GetProperty("type").GetString() == "output_text") text.Append(c.GetProperty("text").GetString());
        ProviderUsage usage = new(null, null, null);
        if (b.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new(Int(u, "input_tokens"), Int(u, "output_tokens"),
                u.TryGetProperty("input_tokens_details", out var d) ? Int(d, "cached_tokens") : null);
        var finish = b.GetProperty("status").GetString() switch
        {
            "completed" => "stop",
            "incomplete" => b.TryGetProperty("incomplete_details", out var i) && i.ValueKind == JsonValueKind.Object
                && i.GetProperty("reason").GetString() == "max_output_tokens" ? "length" : "content_filter",
            var s => s ?? "unknown",
        };
        return new(text.ToString(), finish, usage, b.TryGetProperty("id", out var id) ? id.GetString() : null);
    }

    protected override ProviderStreamEvent? ParseEvent(string type, JsonElement d) => type switch
    {
        "response.output_text.delta" => new(d.GetProperty("delta").GetString(), null),
        "response.completed" or "response.incomplete" => new(null, Parse(d.GetProperty("response"))),
        "response.failed" or "error" => throw new ProviderException("failed", null, "Sağlayıcı akış hatası bildirdi."),
        _ => null,
    };
}

/// <summary>Anthropic Messages API (POST /v1/messages, anthropic-version 2023-06-01).</summary>
public class AnthropicAdapter(HttpClient http) : HttpProviderAdapter(http)
{
    public const string BaseUrl = "https://api.anthropic.com/";
    public override string Provider => "anthropic";
    ProviderUsage _usage = new(null, null, null);
    string? _id;

    protected override HttpRequestMessage Build(ProviderRequest r, string apiKey, bool stream)
    {
        var system = string.Join("\n\n", r.Messages.Where(m => m.Role == "system").SelectMany(m => m.Parts).Select(p => p.Text));
        var body = new JsonObject
        {
            ["model"] = r.Model,
            ["max_tokens"] = r.MaxOutputTokens,
            ["stream"] = stream,
            ["messages"] = new JsonArray(r.Messages.Where(m => m.Role != "system").Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = new JsonArray(m.Parts.Select(p => (JsonNode)(p.Image is not null
                    ? new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = p.MimeType, ["data"] = Convert.ToBase64String(p.Image) },
                    }
                    : new JsonObject { ["type"] = "text", ["text"] = p.Text })).ToArray()),
            }).ToArray()),
        };
        if (system.Length > 0) body["system"] = system;
        if (r.Schema is { } schema)
            body["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = JsonNode.Parse(schema.GetRawText()) }
            };
        var req = Post("v1/messages", body);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        return req;
    }

    static ProviderUsage Usage(JsonElement u) => new(
        Int(u, "input_tokens") is { } i ? i + (Int(u, "cache_read_input_tokens") ?? 0) + (Int(u, "cache_creation_input_tokens") ?? 0) : null,
        Int(u, "output_tokens"), Int(u, "cache_read_input_tokens"));

    static string Finish(string? stop) => stop switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "refusal" => "content_filter",
        _ => stop ?? "unknown",
    };

    protected override ProviderResult Parse(JsonElement b) => new(
        string.Concat(b.GetProperty("content").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString())),
        Finish(b.GetProperty("stop_reason").GetString()),
        b.TryGetProperty("usage", out var u) ? Usage(u) : new(null, null, null),
        b.GetProperty("id").GetString());

    // Adapters are transient (one per request) so stream state lives on the instance.
    protected override ProviderStreamEvent? ParseEvent(string type, JsonElement d)
    {
        switch (type)
        {
            case "message_start":
                var msg = d.GetProperty("message");
                _id = msg.GetProperty("id").GetString();
                _usage = Usage(msg.GetProperty("usage"));
                return null;
            case "content_block_delta" when d.GetProperty("delta").GetProperty("type").GetString() == "text_delta":
                return new(d.GetProperty("delta").GetProperty("text").GetString(), null);
            case "message_delta":
                _usage = _usage with { OutputTokens = d.TryGetProperty("usage", out var u) ? Int(u, "output_tokens") : null };
                return new(null, new ProviderResult("", Finish(d.GetProperty("delta").GetProperty("stop_reason").GetString()), _usage, _id));
            case "error":
                throw new ProviderException(d.GetProperty("error").GetProperty("type").GetString() == "overloaded_error" ? "rejected" : "failed",
                    null, "Sağlayıcı akış hatası bildirdi.");
            default: return null;
        }
    }
}

/// <summary>Development/Testing only. Registered only when Demo:Enabled and the environment is not Production.</summary>
public class DemoAdapter : IProviderAdapter
{
    public string Provider => "demo";

    static ProviderResult Result(ProviderRequest r)
    {
        var text = r.Schema is { } s
            ? Demo(SchemaPolicy.Sample(s))!.ToJsonString()
            : "DEMO yanıtı: bu metin gerçek bir model tarafından üretilmedi. " +
              string.Concat(r.Messages.Last().Parts.Select(p => p.Text)).Trim();
        var input = r.Messages.Sum(m => m.Parts.Sum(p => (p.Text?.Length ?? 0) / 4 + (p.Image is null ? 0 : 1000)));
        return new(text, "stop", new(input, text.Length / 4 + 1, 0), "demo-" + Guid.NewGuid().ToString("N")[..12]);
    }

    static JsonNode? Demo(JsonNode? n)
    {
        if (n is JsonObject o && o.ContainsKey("warnings")) o["warnings"] = new JsonArray("DEMO: çıktı gerçek görselden okunmadı.");
        return n;
    }

    public Task<ProviderResult> GenerateAsync(ProviderRequest r, string apiKey, CancellationToken ct) => Task.FromResult(Result(r));

    public async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest r, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        var result = Result(r);
        foreach (var chunk in result.Text.Chunk(12))
        {
            await Task.Delay(40, ct);
            yield return new(new string(chunk), null);
        }
        yield return new(null, result);
    }
}

public class ProviderRegistry(IServiceProvider sp, IEnumerable<string> enabled)
{
    public bool DemoEnabled => enabled.Contains("demo");
    public IEnumerable<string> Providers => enabled;

    public IProviderAdapter Get(string provider) => provider switch
    {
        _ when !enabled.Contains(provider) => throw new GatewayException(400, "unsupported_capability", $"Sağlayıcı etkin değil: {provider}"),
        "openai" => new OpenAiAdapter(Client("openai")),
        "anthropic" => new AnthropicAdapter(Client("anthropic")),
        _ => new DemoAdapter(),
    };

    /// <summary>Lists models (free endpoint) to prove the key works; false when the provider rejects the key.</summary>
    public async Task<bool> VerifyAsync(string provider, string apiKey, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        if (provider == "openai") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else { req.Headers.Add("x-api-key", apiKey); req.Headers.Add("anthropic-version", "2023-06-01"); }
        using var res = await Client(provider).SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
        if (!res.IsSuccessStatusCode) throw new ProviderException("failed", (int)res.StatusCode, $"Sağlayıcı HTTP {(int)res.StatusCode} döndü.");
        return true;
    }

    HttpClient Client(string name) => ((IHttpClientFactory)sp.GetService(typeof(IHttpClientFactory))!).CreateClient(name);
}

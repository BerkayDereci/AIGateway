using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiGateway.Core;
using AiGateway.Infrastructure;

namespace AiGateway.Tests;

/// <summary>Offline contract tests: request shape and response/stream parsing against recorded-format fixtures.</summary>
public class AdapterContractTests
{
    static readonly JsonElement Schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "examples/workout.schema.json"))).RootElement;

    static string Root()
    {
        var d = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(d, "AiGateway.slnx"))) d = Path.GetDirectoryName(d)!;
        return d;
    }

    static ProviderRequest Request(JsonElement? schema = null) => new("m-1",
        [
            new("system", [new("Sadece görüneni çıkar.")]),
            new("user", [new("Programı oku"), new(null, [1, 2, 3], "image/png")]),
        ], 300, "workout_program", schema);

    static (T Adapter, FakeProvider Fake) Make<T>(Func<HttpClient, T> ctor, string baseUrl)
    {
        var fake = new FakeProvider();
        return (ctor(new HttpClient(fake) { BaseAddress = new Uri(baseUrl) }), fake);
    }

    static Stream SseStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task OpenAi_maps_text_image_and_schema_and_parses_usage()
    {
        var (a, fake) = Make(h => new OpenAiAdapter(h), OpenAiAdapter.BaseUrl);
        fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new
        {
            id = "resp_1", status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "{\"a\":1}" } } } },
            usage = new { input_tokens = 50, output_tokens = 7, input_tokens_details = new { cached_tokens = 10 } },
        }));
        var r = await a.GenerateAsync(Request(Schema), "sk-x", default);

        var (url, body) = fake.Calls.Single();
        Assert.Equal("https://api.openai.com/v1/responses", url);
        var b = JsonNode.Parse(body)!;
        Assert.Equal("m-1", (string)b["model"]!);
        Assert.Equal(300, (int)b["max_output_tokens"]!);
        Assert.False((bool)b["store"]!);
        Assert.Equal("system", (string)b["input"]![0]!["role"]!);
        Assert.Equal("input_text", (string)b["input"]![1]!["content"]![0]!["type"]!);
        Assert.Equal("input_image", (string)b["input"]![1]!["content"]![1]!["type"]!);
        Assert.Equal("data:image/png;base64,AQID", (string)b["input"]![1]!["content"]![1]!["image_url"]!);
        Assert.Equal("json_schema", (string)b["text"]!["format"]!["type"]!);
        Assert.True((bool)b["text"]!["format"]!["strict"]!);
        Assert.Equal(("{\"a\":1}", "stop", 50, 7, 10, "resp_1"),
            (r.Text, r.FinishReason, r.Usage.InputTokens!.Value, r.Usage.OutputTokens!.Value, r.Usage.CachedInputTokens!.Value, r.ProviderRequestId));
    }

    [Fact]
    public async Task OpenAi_stream_parses_deltas_and_final_usage()
    {
        var (a, fake) = Make(h => new OpenAiAdapter(h), OpenAiAdapter.BaseUrl);
        fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Sse(SseStream(
            "event: response.created\ndata: {\"type\":\"response.created\"}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Mer\"}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"haba ğ\"}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}}\n\n")));
        var events = new List<ProviderStreamEvent>();
        await foreach (var e in a.StreamAsync(Request(), "sk", default)) events.Add(e);
        Assert.Equal("Merhaba ğ", string.Concat(events.Select(e => e.Delta)));
        Assert.Equal(("length", 3, 4), (events[^1].Final!.FinishReason, events[^1].Final!.Usage.InputTokens!.Value, events[^1].Final!.Usage.OutputTokens!.Value));
        Assert.Contains("\"stream\":true", fake.Calls.Single().Body);
    }

    [Fact]
    public async Task Anthropic_maps_system_image_and_output_config()
    {
        var (a, fake) = Make(h => new AnthropicAdapter(h), AnthropicAdapter.BaseUrl);
        HttpRequestMessage? seen = null;
        fake.Respond = (req, _, _) =>
        {
            seen = req;
            return Task.FromResult(FakeProvider.Json(new
            {
                id = "msg_1", type = "message", stop_reason = "end_turn",
                content = new[] { new { type = "text", text = "{}" } },
                usage = new { input_tokens = 20, output_tokens = 5, cache_read_input_tokens = 4 },
            }));
        };
        var r = await a.GenerateAsync(Request(Schema), "ant-key", default);

        var b = JsonNode.Parse(fake.Calls.Single().Body)!;
        Assert.Equal("https://api.anthropic.com/v1/messages", fake.Calls.Single().Url);
        Assert.Equal("ant-key", seen!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", seen.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("Sadece görüneni çıkar.", (string)b["system"]!);
        Assert.Single(b["messages"]!.AsArray()); // system is not a message role for Anthropic
        Assert.Equal("base64", (string)b["messages"]![0]!["content"]![1]!["source"]!["type"]!);
        Assert.Equal("image/png", (string)b["messages"]![0]!["content"]![1]!["source"]!["media_type"]!);
        Assert.Equal("json_schema", (string)b["output_config"]!["format"]!["type"]!);
        Assert.Equal(300, (int)b["max_tokens"]!);
        Assert.Equal(("stop", 24, 5, 4), (r.FinishReason, r.Usage.InputTokens!.Value, r.Usage.OutputTokens!.Value, r.Usage.CachedInputTokens!.Value));
    }

    [Fact]
    public async Task Anthropic_stream_parses_events()
    {
        var (a, fake) = Make(h => new AnthropicAdapter(h), AnthropicAdapter.BaseUrl);
        fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Sse(SseStream(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_9\",\"usage\":{\"input_tokens\":25,\"output_tokens\":1}}}\n\n" +
            "event: ping\ndata: {\"type\":\"ping\"}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"},\"usage\":{\"output_tokens\":15}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n")));
        var events = new List<ProviderStreamEvent>();
        await foreach (var e in a.StreamAsync(Request(), "k", default)) events.Add(e);
        Assert.Equal("Hello", string.Concat(events.Select(e => e.Delta)));
        var final = events[^1].Final!;
        Assert.Equal(("length", 25, 15, "msg_9"), (final.FinishReason, final.Usage.InputTokens!.Value, final.Usage.OutputTokens!.Value, final.ProviderRequestId));
    }

    [Theory]
    [InlineData(429, "rejected")]
    [InlineData(529, "rejected")]
    [InlineData(401, "failed")]
    [InlineData(400, "failed")]
    public async Task Http_errors_are_classified_without_leaking_body(int status, string kind)
    {
        var (a, fake) = Make(h => new AnthropicAdapter(h), AnthropicAdapter.BaseUrl);
        fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Json(new { error = "secret-provider-detail" }, status));
        var e = await Assert.ThrowsAsync<ProviderException>(() => a.GenerateAsync(Request(), "k", default));
        Assert.Equal(kind, e.Kind);
        Assert.DoesNotContain("secret-provider-detail", e.Message);
    }

    [Fact]
    public async Task Stream_cut_without_terminal_event_is_ambiguous()
    {
        var (a, fake) = Make(h => new OpenAiAdapter(h), OpenAiAdapter.BaseUrl);
        fake.Respond = (_, _, _) => Task.FromResult(FakeProvider.Sse(SseStream(
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"x\"}\n\n")));
        var e = await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var _ in a.StreamAsync(Request(), "k", default)) { } });
        Assert.Equal("ambiguous", e.Kind);
    }

    [Fact]
    public void Schema_policy_accepts_workout_schema_for_both_providers()
    {
        SchemaPolicy.Check(Schema, requireAllPropertiesRequired: true);
        SchemaPolicy.Check(Schema, requireAllPropertiesRequired: false);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","minLength":2}},"required":["a"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""")]
    [InlineData("""{"type":"object","properties":{"a":{"$ref":"#/x"}},"required":["a"],"additionalProperties":false}""")]
    [InlineData("""{"type":"array"}""")]
    public void Schema_policy_rejects_unsupported(string schema)
    {
        var e = Assert.Throws<GatewayException>(() => SchemaPolicy.Check(JsonDocument.Parse(schema).RootElement, false));
        Assert.Equal("unsupported_schema", e.Code);
    }

    [Fact]
    public void Openai_strict_requires_every_property()
    {
        var s = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["a"],"additionalProperties":false}""").RootElement;
        SchemaPolicy.Check(s, false);
        Assert.Throws<GatewayException>(() => SchemaPolicy.Check(s, true));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"title":null,"days":[],"warnings":[],"extra":1}""")]
    [InlineData("""{"title":3,"days":[],"warnings":[]}""")]
    public void Output_validation_fails_explicitly(string output)
    {
        var e = Assert.Throws<GatewayException>(() => SchemaPolicy.ParseAndValidate(output, Schema));
        Assert.Equal((422, "output_validation_failed"), (e.Status, e.Code));
    }

    [Fact]
    public void Valid_output_parses_and_demo_sample_satisfies_schema()
    {
        var json = SchemaPolicy.ParseAndValidate("""{"title":"A","days":[{"name":"Gün 1","exercises":[]}],"warnings":[]}""", Schema);
        Assert.Equal("A", json.GetProperty("title").GetString());
        SchemaPolicy.ParseAndValidate(SchemaPolicy.Sample(Schema)!.ToJsonString(), Schema);
    }
}

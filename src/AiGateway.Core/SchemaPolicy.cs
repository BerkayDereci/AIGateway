using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AiGateway.Core;

/// <summary>Supported JSON Schema subset: the intersection documented in docs/provider-compatibility.md.</summary>
public static class SchemaPolicy
{
    static readonly HashSet<string> Allowed =
        ["type", "properties", "required", "additionalProperties", "items", "enum", "description", "title", "anyOf", "format"];
    const int MaxDepth = 10, MaxProperties = 300;

    public static void Check(JsonElement schema, bool requireAllPropertiesRequired)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var t) || t.GetString() != "object")
            throw Fail("Kök şema type: object olmalı.");
        var count = 0;
        Walk(schema, 0, requireAllPropertiesRequired, ref count);
    }

    static void Walk(JsonElement s, int depth, bool allRequired, ref int count)
    {
        if (depth > MaxDepth) throw Fail($"Şema derinliği {MaxDepth} sınırını aşıyor.");
        if (s.ValueKind != JsonValueKind.Object) throw Fail("Şema düğümü nesne olmalı.");
        foreach (var p in s.EnumerateObject())
            if (!Allowed.Contains(p.Name)) throw Fail($"Desteklenmeyen şema anahtar kelimesi: {p.Name}");

        if (s.TryGetProperty("properties", out var props))
        {
            if (!s.TryGetProperty("additionalProperties", out var ap) || ap.ValueKind != JsonValueKind.False)
                throw Fail("Her nesne additionalProperties: false tanımlamalı.");
            var required = s.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
            foreach (var p in props.EnumerateObject())
            {
                if (++count > MaxProperties) throw Fail($"Şema {MaxProperties} alandan fazla içeriyor.");
                if (allRequired && !required.Contains(p.Name)) throw Fail($"'{p.Name}' alanı required listesinde olmalı (OpenAI strict).");
                Walk(p.Value, depth + 1, allRequired, ref count);
            }
        }
        if (s.TryGetProperty("items", out var items)) Walk(items, depth + 1, allRequired, ref count);
        if (s.TryGetProperty("anyOf", out var any)) foreach (var a in any.EnumerateArray()) Walk(a, depth + 1, allRequired, ref count);
    }

    static GatewayException Fail(string m) => new(400, "unsupported_schema", m);

    /// <summary>Parses model text and validates it; throws output_validation_failed otherwise.</summary>
    public static JsonElement ParseAndValidate(string text, JsonElement schema)
    {
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new GatewayException(422, "output_validation_failed", "Model çıktısı geçerli JSON değil."); }
        var result = JsonSchema.FromText(schema.GetRawText()).Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        if (!result.IsValid) throw new GatewayException(422, "output_validation_failed", "Model çıktısı şemaya uymuyor.");
        return json;
    }

    /// <summary>Smallest instance satisfying a supported schema; used only by the demo provider.</summary>
    public static JsonNode? Sample(JsonElement s)
    {
        if (s.TryGetProperty("enum", out var e)) return JsonNode.Parse(e[0].GetRawText());
        if (s.TryGetProperty("anyOf", out var any)) return Sample(any[0]);
        var types = !s.TryGetProperty("type", out var t) ? [] :
            t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()!).ToList() : [t.GetString()!];
        if (types.Contains("null")) return null;
        return types.FirstOrDefault() switch
        {
            "object" => new JsonObject(s.TryGetProperty("properties", out var ps)
                ? ps.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, Sample(p.Value))) : []),
            "array" => new JsonArray(),
            "string" => "",
            "integer" or "number" => 0,
            "boolean" => false,
            _ => null
        };
    }
}

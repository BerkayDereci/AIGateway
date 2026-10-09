// AI Gateway sample client: text, image upload + structured output, SSE streaming with cancellation.
// Usage: AIGATEWAY_URL=http://localhost:5080 AIGATEWAY_KEY=pgw_... dotnet run -- text|image <file>|stream [cancelAfterSeconds]
// The key belongs to your backend's secret manager; never ship it in a mobile/web client.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var baseUrl = Environment.GetEnvironmentVariable("AIGATEWAY_URL") ?? "http://localhost:5080";
var key = Environment.GetEnvironmentVariable("AIGATEWAY_KEY") ?? throw new InvalidOperationException("Set AIGATEWAY_KEY.");
var profile = Environment.GetEnvironmentVariable("AIGATEWAY_PROFILE") ?? "workout-extraction";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(150) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

JsonObject Text(string role, string text) => new() { ["role"] = role, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

switch (args.FirstOrDefault())
{
    case "text":
    {
        var body = new JsonObject { ["profile"] = profile, ["messages"] = new JsonArray(Text("user", "Merhaba! Kısaca kendini tanıt.")) };
        // Idempotency-Key makes a network retry safe: a completed request is replayed, never re-billed.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/generations") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var res = await http.SendAsync(req);
        Console.WriteLine($"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        break;
    }
    case "image":
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("image <file> required");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".png") ? "image/png" : path.EndsWith(".webp") ? "image/webp" : "image/jpeg");
        form.Add(file, "file", Path.GetFileName(path));
        var upload = await http.PostAsync("/api/v1/uploads", form);
        var asset = await upload.Content.ReadFromJsonAsync<JsonObject>();
        if (!upload.IsSuccessStatusCode) { Console.WriteLine(asset); return; }

        var request = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "workout.request.json")))!;
        request["profile"] = profile;
        request["messages"]![1]!["content"]![1]!["assetId"] = (string)asset!["assetId"]!;
        var res = await http.PostAsJsonAsync("/api/v1/generations", request);
        Console.WriteLine($"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        break;
    }
    case "stream":
    {
        using var cts = new CancellationTokenSource();
        if (double.TryParse(args.ElementAtOrDefault(1), System.Globalization.CultureInfo.InvariantCulture, out var seconds)) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        var body = new JsonObject { ["profile"] = profile, ["stream"] = true, ["messages"] = new JsonArray(Text("user", "Uzun bir paragraf yaz.")) };
        try
        {
            using var res = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/generations") { Content = JsonContent.Create(body) },
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode) { Console.WriteLine(await res.Content.ReadAsStringAsync()); return; }
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
            string? evt = null;
            var data = new StringBuilder();
            // Parse at frame boundaries (blank line); network chunks are not text boundaries.
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("event:")) evt = line[6..].Trim();
                else if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
                else if (line.Length == 0 && data.Length > 0)
                {
                    var json = JsonNode.Parse(data.ToString())!;
                    if (evt == "text.delta") Console.Write((string)json["text"]!);
                    else Console.WriteLine($"\n[{evt}] {json.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}");
                    evt = null; data.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the connection cancels the provider call; the gateway records the accounting state.
            Console.WriteLine("\n[cancelled by client]");
        }
        break;
    }
    default:
        Console.WriteLine("usage: dotnet run -- text | image <file> | stream [cancelAfterSeconds]");
        break;
}

using AiGateway.Core;

namespace AiGateway.Api;

public static class Errors
{
    public static Guid RequestId(HttpContext ctx) => (Guid)(ctx.Items["requestId"] ?? Guid.Empty);

    public static object Problem(HttpContext ctx, GatewayException e) => new
    {
        type = $"https://aigateway.local/errors/{e.Code}",
        title = e.Code,
        status = e.Status,
        detail = e.Message,
        requestId = RequestId(ctx),
        code = e.Code,
        retryable = e.Retryable,
    };

    public static async ValueTask Write(HttpContext ctx, GatewayException e)
    {
        ctx.Response.StatusCode = e.Status;
        if (e.RetryAfter is { } ra) ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(ra.TotalSeconds)).ToString();
        await ctx.Response.WriteAsJsonAsync(Problem(ctx, e), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}

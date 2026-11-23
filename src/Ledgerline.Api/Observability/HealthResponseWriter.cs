using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledgerline.Api.Observability;

/// <summary>Writes health reports as JSON, including each check's data (e.g. outbox backlog and lag).</summary>
internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data.Count == 0 ? null : e.Value.Data,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted);
    }
}

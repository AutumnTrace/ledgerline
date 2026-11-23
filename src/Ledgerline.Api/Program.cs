using System.Text.Json.Serialization;
using Ledgerline.Api.Endpoints;
using Ledgerline.Api.Errors;
using Ledgerline.Api.Observability;
using Ledgerline.Application;
using Ledgerline.Infrastructure;
using Ledgerline.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddLedgerObservability();

builder.Services
    .AddLedgerApplication()
    .AddLedgerInfrastructure(builder.Configuration);

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions.TryAdd("traceId", ctx.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Ledgerline API";
    document.Info.Description =
        "Double-entry wallet ledger. Every command is idempotent on the caller's transaction id: "
        + "the first call returns 201, a replay returns 200 with the original result and `Idempotent-Replayed: true`. "
        + "Amounts are integer minor units with an ISO 4217 currency.";
    return Task.CompletedTask;
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<LedgerDbContext>("database", tags: ["ready"]);

var app = builder.Build();

await app.Services.InitializeLedgerDatabaseAsync();

// Request logging wraps the exception handler, so it records the final status code (422, 409...)
// instead of seeing every handled business rejection as an unhandled exception.
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal) ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference(o => o.Title = "Ledgerline API");
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapGroup("/v1")
    .MapCommandEndpoints()
    .MapQueryEndpoints();

await app.RunAsync();

/// <summary>Entry point, public so <c>WebApplicationFactory</c> can host the app in tests.</summary>
public partial class Program;

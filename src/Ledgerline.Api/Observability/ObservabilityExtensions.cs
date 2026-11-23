using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Ledgerline.Application.Telemetry;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Ledgerline.Api.Observability;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Serilog for logs (configured from appsettings), OpenTelemetry for traces and metrics. Exporters
    /// are opt-in: OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, Azure Monitor (Application
    /// Insights) when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set.
    /// </summary>
    public static WebApplicationBuilder AddLedgerObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", "ledgerline"));

        var version = typeof(ObservabilityExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("ledgerline", serviceVersion: version))
            .WithTracing(tracing => tracing
                .AddSource(LedgerTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter(LedgerMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitor();
        }

        return builder;
    }
}

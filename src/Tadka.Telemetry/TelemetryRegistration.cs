using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Tadka.Telemetry;

/// <summary>
/// One call — <c>builder.AddTadkaTelemetry("Tadka.Xxx")</c> — wires all three observability pillars (ADR-040)
/// into a service: structured JSON logs (Serilog), metrics + traces (OpenTelemetry → OTLP → Collector).
///
/// The OTLP export is GATED on the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> env var: unset (the test suite,
/// single-process dev) ⇒ no exporter is wired and behaviour is identical to Day 12. This mirrors the
/// Kafka-off gate so the 39/39 tests stay green without an observability stack running.
/// </summary>
public static class TelemetryRegistration
{
    public const string OtlpEndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static WebApplicationBuilder AddTadkaTelemetry(this WebApplicationBuilder builder, string serviceName)
    {
        // ── PILLAR 1: structured logs ────────────────────────────────────────────────────────────────
        // JSON to console, every line enriched with service.name + trace_id/span_id (so you grep one trace
        // across all 5 services). Works with NO backend, so tests + single-process dev still get clean logs.
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", serviceName)
            .Enrich.With(new ActivityEnricher())
            .WriteTo.Console(new CompactJsonFormatter()));

        // ── PILLARS 2 & 3: metrics + traces over OTLP ────────────────────────────────────────────────
        // GATED: no endpoint ⇒ no exporter. Logs above still work; the app behaves exactly as Day 12.
        var otlp = Environment.GetEnvironmentVariable(OtlpEndpointEnvVar);
        if (string.IsNullOrWhiteSpace(otlp))
            return builder;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddSource(TadkaDiagnostics.ActivitySourceName)       // our custom business spans + Kafka spans
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = http => !IsNoise(http.Request.Path.Value))   // skip /health + /metrics noise
                .AddHttpClientInstrumentation()                       // outbound HTTP (incl. YARP forwarding)
                .AddOtlpExporter())                                   // endpoint comes from the env var
            .WithMetrics(m => m
                .AddMeter(TadkaDiagnostics.MeterName)                 // our custom business metrics
                .AddAspNetCoreInstrumentation()                       // RED: request rate/errors/duration
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()                          // GC, threadpool, etc.
                .AddOtlpExporter());

        return builder;
    }

    // The "when NOT to trace" list (ADR-040): health checks fire every few seconds per service and the
    // metrics scrape hits /metrics constantly — pure noise that would drown the real order traces.
    private static bool IsNoise(string? path) =>
        path is not null &&
        (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Stamps every log line with the current trace_id/span_id so logs tie back to a Jaeger trace (ADR-040/041).</summary>
internal sealed class ActivityEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace_id", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span_id", activity.SpanId.ToString()));
    }
}

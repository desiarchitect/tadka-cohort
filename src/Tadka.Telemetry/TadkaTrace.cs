using System.Diagnostics;

namespace Tadka.Telemetry;

/// <summary>
/// W3C trace-context helpers for crossing the <b>Kafka</b> boundary (ADR-041). HTTP hops propagate
/// <c>traceparent</c> automatically (the .NET HttpClient + ASP.NET Core instrumentation do it), but Kafka
/// does not — and because we publish through the transactional <b>Outbox</b> (ADR-028), the originating
/// request's <see cref="Activity"/> is long gone by the time the relay produces. So we:
///   1. capture <see cref="CurrentTraceParent"/> when the outbox row is written (store it on the row),
///   2. re-inject it as a Kafka <c>traceparent</c> header at relay time, and
///   3. <see cref="ParseContext"/> it on the consumer to start the processing span as a remote child.
/// The result: the choreographed saga (order → payment → delivery) shows up as ONE trace, not four.
///
/// Deliberately built on <c>System.Diagnostics</c> only (no Confluent.Kafka / OTEL.Api dependency) so this
/// helper is usable from every service and the gateway without dragging Kafka into projects that don't use it.
/// </summary>
public static class TadkaTrace
{
    /// <summary>The W3C header/key under which the traceparent travels on a Kafka message.</summary>
    public const string TraceParentHeader = "traceparent";

    /// <summary>The current activity's W3C id (<c>00-traceid-spanid-flags</c>), or null when no span/telemetry is active.</summary>
    public static string? CurrentTraceParent() => Activity.Current?.Id;

    /// <summary>Parse a stored/received traceparent into a remote <see cref="ActivityContext"/> for use as a span parent.
    /// Returns <c>default</c> (no parent) when the string is null/invalid — so a missing header degrades to a new root.</summary>
    public static ActivityContext ParseContext(string? traceParent) =>
        ActivityContext.TryParse(traceParent, traceState: null, isRemote: true, out var ctx) ? ctx : default;
}

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// DEMO LEVER (Day 11, ADR-046): does the refund-compensation saga run as choreography (the default,
/// every day-9-11 saga step so far — handlers react to events independently, no central coordinator)
/// or orchestration (one place, <see cref="RefundSagaOrchestrator"/>, explicitly sequences the steps)?
/// Functionally identical either way — same DB writes, same events — the difference is entirely in
/// HOW the sequencing is expressed and observed (see ADR-046 for the trade-off).
/// </summary>
public sealed class SagaOptions
{
    public const string SectionName = "Saga";

    public string Mode { get; set; } = "Choreography";

    public bool IsOrchestration => string.Equals(Mode, "Orchestration", StringComparison.OrdinalIgnoreCase);
}

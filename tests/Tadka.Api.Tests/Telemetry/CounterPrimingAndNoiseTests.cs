using System.Diagnostics.Metrics;
using Tadka.Telemetry;

namespace Tadka.Api.Tests.Telemetry;

// Two small guarantees behind the Day 13 demos, no Collector or Docker needed.
public class CounterPrimingAndNoiseTests
{
    [Fact]
    public void Priming_creates_the_success_and_failed_payment_series_at_zero()
    {
        var seen = new List<(string Status, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == TadkaDiagnostics.MeterName && instrument.Name == "tadka.payment.result")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "status") seen.Add((tag.Value?.ToString() ?? "", value));
        });
        listener.Start();

        TadkaDiagnostics.PrimePaymentCounters();

        // Both series now exist with a value of 0, so the first real failure is a change Prometheus can measure.
        Assert.Contains(("success", 0L), seen);
        Assert.Contains(("failed", 0L), seen);
    }

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/metrics", true)]
    [InlineData("/", true)]
    [InlineData("/api/v1/orders", false)]
    [InlineData("/api/v1/payments/abc", false)]
    [InlineData(null, false)]
    public void Health_metrics_and_root_requests_are_not_traced(string? path, bool expectedNoise)
        => Assert.Equal(expectedNoise, TelemetryRegistration.IsNoise(path));
}

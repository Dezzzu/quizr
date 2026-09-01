using System.Diagnostics.Metrics;
using Quizr.App.Telemetry;

namespace Quizr.App.Tests;

// A real Meter with nobody subscribed — which is exactly what production looks like when no
// OTLP endpoint is configured, so a test that doesn't care about telemetry gets the same
// no-op Adds the deployed bot gets. Tests that do care pass this to a MetricCollector.
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }

        _meters.Clear();
    }

    public static QuizrMetrics Metrics() => new(new TestMeterFactory());
}

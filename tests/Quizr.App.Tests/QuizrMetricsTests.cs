using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Quizr.App.Telemetry;

namespace Quizr.App.Tests;

// The instrument names and tag keys are what the Grafana dashboards and alert rules are
// written against, so they're part of the contract with the deployment rather than an
// implementation detail — renaming one silently breaks a dashboard, which is exactly the kind
// of failure nobody notices until they need it.
public class QuizrMetricsTests
{
    // Program.cs registers these two lines and nothing else resolves QuizrMetrics in a test,
    // so without this a missing AddMetrics() would compile, pass every test, and then fail at
    // startup — which on this deployment means in production, since main autodeploys.
    [Test]
    public void ResolvesFromTheSameRegistrationTheCompositionRootUses()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<QuizrMetrics>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<QuizrMetrics>().Should().NotBeNull();
    }

    [Test]
    public void RecordsHandledUpdates()
    {
        using var factory = new TestMeterFactory();
        var metrics = new QuizrMetrics(factory);
        using var collector = new MetricCollector<long>(factory, QuizrMetrics.MeterName, "quizr.updates");

        metrics.RecordUpdate();
        metrics.RecordUpdate();

        collector.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(2);
    }

    // The heartbeat DEPLOY.md's "configure no health check" leans on: the alert is on this
    // counter going quiet, so it has to advance once per tick and be findable by this name.
    [Test]
    public void RecordsSchedulerTicks()
    {
        using var factory = new TestMeterFactory();
        var metrics = new QuizrMetrics(factory);
        using var collector = new MetricCollector<long>(factory, QuizrMetrics.MeterName, "quizr.scheduler.ticks");

        metrics.RecordSchedulerTick();

        collector.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(1);
    }

    [Test]
    public void TagsAnExceptionWithItsTypeAndWhereItWasCaught()
    {
        using var factory = new TestMeterFactory();
        var metrics = new QuizrMetrics(factory);
        using var collector = new MetricCollector<long>(factory, QuizrMetrics.MeterName, "quizr.exceptions");

        metrics.RecordException(new InvalidOperationException("boom"), QuizrMetrics.SchedulerGameSource);

        var measurement = collector.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Tags["error.type"].Should().Be("System.InvalidOperationException");
        measurement.Tags["quizr.source"].Should().Be(QuizrMetrics.SchedulerGameSource);
    }

    // The message is deliberately not a tag: it carries chat titles and player names, which
    // would put unbounded text into a label and multiply the time series by every distinct
    // failure string.
    [Test]
    public void DoesNotPutTheExceptionMessageInATag()
    {
        using var factory = new TestMeterFactory();
        var metrics = new QuizrMetrics(factory);
        using var collector = new MetricCollector<long>(factory, QuizrMetrics.MeterName, "quizr.exceptions");

        metrics.RecordException(new InvalidOperationException("Лена's game blew up"), QuizrMetrics.UpdateSource);

        var measurement = collector.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        var tagValues = measurement.Tags.Values.OfType<string>().ToList();
        tagValues.Should().NotContain(value => value.Contains("Лена", StringComparison.Ordinal));
    }
}

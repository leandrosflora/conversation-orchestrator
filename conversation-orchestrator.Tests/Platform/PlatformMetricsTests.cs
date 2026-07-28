using conversation_orchestrator.Platform;
using Xunit;

namespace conversation_orchestrator.Tests.Platform;

public class PlatformMetricsTests
{
    [Fact]
    public void SetGauge_RendersCurrentValue()
    {
        var metrics = new PlatformMetrics();

        metrics.SetGauge("orchestrator_outbox_oldest_unresolved_seconds", 42.5);

        Assert.Contains("orchestrator_outbox_oldest_unresolved_seconds 42.5", metrics.Render());
    }

    [Fact]
    public void SetGauge_LaterCallReplacesRatherThanAccumulates()
    {
        var metrics = new PlatformMetrics();

        metrics.SetGauge("orchestrator_outbox_oldest_unresolved_seconds", 300);
        metrics.SetGauge("orchestrator_outbox_oldest_unresolved_seconds", 0);

        var rendered = metrics.Render();
        Assert.Contains("orchestrator_outbox_oldest_unresolved_seconds 0", rendered);
        Assert.DoesNotContain("orchestrator_outbox_oldest_unresolved_seconds 300", rendered);
    }

    [Fact]
    public void SetGauge_WithLabels_RendersDistinctSeries()
    {
        var metrics = new PlatformMetrics();

        metrics.SetGauge("queue_depth", 3, ("queue", "a"));
        metrics.SetGauge("queue_depth", 7, ("queue", "b"));

        var rendered = metrics.Render();
        Assert.Contains("queue_depth{queue=\"a\"} 3", rendered);
        Assert.Contains("queue_depth{queue=\"b\"} 7", rendered);
    }
}

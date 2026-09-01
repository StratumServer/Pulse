using System.Globalization;
using Xunit;

namespace Pulse.Tests;

public class PrometheusTextTests
{
    [Fact]
    public void Render_Writes_CounterFamily()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_server_ticks_total", MetricKind.Counter, "Server ticks.", 42),
        ]);

        Assert.Equal(
            "# HELP pulse_server_ticks_total Server ticks.\n" +
            "# TYPE pulse_server_ticks_total counter\n" +
            "pulse_server_ticks_total 42\n",
            text);
    }

    [Fact]
    public void Render_Writes_GaugeFamily()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_players_online", MetricKind.Gauge, "Players connected.", 3),
        ]);

        Assert.Equal(
            "# HELP pulse_players_online Players connected.\n" +
            "# TYPE pulse_players_online gauge\n" +
            "pulse_players_online 3\n",
            text);
    }

    [Fact]
    public void Render_Writes_HistogramFamily_WithCumulativeBucketsAndInf()
    {
        string text = PrometheusText.Render([
            new MetricSample("pulse_server_tick_seconds", MetricKind.Histogram, "Tick period.", 0)
            {
                Bounds = [0.025, 0.05, 0.1],
                Buckets = [2, 0, 1],
                Sum = 0.44,
                Count = 5,
            },
        ]);

        Assert.Equal(
            "# HELP pulse_server_tick_seconds Tick period.\n" +
            "# TYPE pulse_server_tick_seconds histogram\n" +
            "pulse_server_tick_seconds_bucket{le=\"0.025\"} 2\n" +
            "pulse_server_tick_seconds_bucket{le=\"0.05\"} 2\n" +
            "pulse_server_tick_seconds_bucket{le=\"0.1\"} 3\n" +
            "pulse_server_tick_seconds_bucket{le=\"+Inf\"} 5\n" +
            "pulse_server_tick_seconds_sum 0.44\n" +
            "pulse_server_tick_seconds_count 5\n",
            text);
    }

    [Fact]
    public void Render_Writes_EveryFamilyInOrder()
    {
        string text = PrometheusText.Render([
            new MetricSample("a_total", MetricKind.Counter, "A.", 1),
            new MetricSample("b_gauge", MetricKind.Gauge, "B.", 2),
        ]);

        Assert.Equal(
            "# HELP a_total A.\n# TYPE a_total counter\na_total 1\n" +
            "# HELP b_gauge B.\n# TYPE b_gauge gauge\nb_gauge 2\n",
            text);
    }

    [Fact]
    public void Render_Uses_InvariantCulture_UnderAFrenchLocale()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // The dev machine runs fr-FR, where the default double formatter writes "0,025".
            // A comma decimal separator makes the whole exposition unparseable.
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            string text = PrometheusText.Render([
                new MetricSample("pulse_server_tick_seconds", MetricKind.Histogram, "Tick period.", 0)
                {
                    Bounds = [0.025, 1.5],
                    Buckets = [1, 1],
                    Sum = 1.525,
                    Count = 2,
                },
                new MetricSample("pulse_server_tick_budget_seconds", MetricKind.Gauge, "Budget.", 0.0334),
            ]);

            Assert.DoesNotContain(",", text);
            Assert.Contains("le=\"0.025\"", text);
            Assert.Contains("le=\"1.5\"", text);
            Assert.Contains("pulse_server_tick_seconds_sum 1.525\n", text);
            Assert.Contains("pulse_server_tick_budget_seconds 0.0334\n", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Render_Writes_NonFiniteValues_TheWayPrometheusSpellsThem()
    {
        string text = PrometheusText.Render([
            new MetricSample("nan_gauge", MetricKind.Gauge, "N.", double.NaN),
            new MetricSample("pos_gauge", MetricKind.Gauge, "P.", double.PositiveInfinity),
            new MetricSample("neg_gauge", MetricKind.Gauge, "M.", double.NegativeInfinity),
        ]);

        Assert.Contains("nan_gauge NaN\n", text);
        Assert.Contains("pos_gauge +Inf\n", text);
        Assert.Contains("neg_gauge -Inf\n", text);
    }

    [Fact]
    public void Render_Writes_Nothing_ForNoSamples()
    {
        Assert.Equal(string.Empty, PrometheusText.Render([]));
    }
}

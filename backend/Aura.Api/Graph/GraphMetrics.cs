using Prometheus;

namespace Aura.Api.Graph;

internal static class GraphMetrics
{
    private static readonly Counter Projections = Metrics.CreateCounter(
        "aura_graph_projection_total",
        "ArangoDB graph projection outcomes.",
        new CounterConfiguration { LabelNames = ["event_type", "result"] });
    private static readonly Histogram ProjectionDuration = Metrics.CreateHistogram(
        "aura_graph_projection_duration_seconds",
        "ArangoDB graph projection latency.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.005, 2, 14) });
    private static readonly Counter Queries = Metrics.CreateCounter(
        "aura_graph_query_total",
        "ArangoDB graph query outcomes.",
        new CounterConfiguration { LabelNames = ["operation", "result"] });
    private static readonly Histogram QueryDuration = Metrics.CreateHistogram(
        "aura_graph_query_duration_seconds",
        "ArangoDB graph query latency.",
        new HistogramConfiguration { LabelNames = ["operation"], Buckets = Histogram.ExponentialBuckets(0.005, 2, 14) });
    private static readonly Histogram QueryRows = Metrics.CreateHistogram(
        "aura_graph_query_rows",
        "Rows returned by graph queries.",
        new HistogramConfiguration { LabelNames = ["operation"], Buckets = [0, 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000] });
    private static readonly Gauge RebuildDocuments = Metrics.CreateGauge(
        "aura_graph_rebuild_documents",
        "Documents written by the latest completed graph rebuild.",
        new GaugeConfiguration { LabelNames = ["kind"] });
    private static readonly Counter Rebuilds = Metrics.CreateCounter(
        "aura_graph_rebuild_total",
        "Graph rebuild outcomes.",
        new CounterConfiguration { LabelNames = ["result"] });

    internal static ProjectionTimer TrackProjection(string eventType) => new(eventType);
    internal static QueryTimer TrackQuery(string operation) => new(operation);
    internal static void ObserveRebuild(long vertices, long edges, bool succeeded)
    {
        Rebuilds.WithLabels(succeeded ? "success" : "failure").Inc();
        RebuildDocuments.WithLabels("vertices").Set(vertices);
        RebuildDocuments.WithLabels("edges").Set(edges);
    }

    internal sealed class ProjectionTimer(string eventType) : IDisposable
    {
        private readonly IDisposable _timer = ProjectionDuration.NewTimer();
        private bool _succeeded;
        internal void Success() => _succeeded = true;
        public void Dispose()
        {
            _timer.Dispose();
            Projections.WithLabels(eventType, _succeeded ? "success" : "failure").Inc();
        }
    }

    internal sealed class QueryTimer(string operation) : IDisposable
    {
        private readonly IDisposable _timer = QueryDuration.WithLabels(operation).NewTimer();
        private string _result = "failure";
        private int? _rows;
        internal void Success(int rows)
        {
            _result = "success";
            _rows = rows;
        }
        internal void Timeout() => _result = "timeout";
        public void Dispose()
        {
            _timer.Dispose();
            Queries.WithLabels(operation, _result).Inc();
            if (_rows.HasValue) QueryRows.WithLabels(operation).Observe(_rows.Value);
        }
    }
}

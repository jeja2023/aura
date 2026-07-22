using Prometheus;

namespace Aura.Api.Vector;

internal static class VectorIndexMetrics
{
    private static readonly Counter Searches = Metrics.CreateCounter(
        "aura_vector_search_total",
        "Vector searches by engine and result.",
        new CounterConfiguration { LabelNames = ["engine", "result"] });
    private static readonly Histogram SearchDuration = Metrics.CreateHistogram(
        "aura_vector_search_duration_seconds",
        "Vector search latency by engine.",
        new HistogramConfiguration { LabelNames = ["engine"], Buckets = Histogram.ExponentialBuckets(0.0025, 2, 14) });
    private static readonly Histogram SearchHits = Metrics.CreateHistogram(
        "aura_vector_search_hits",
        "Number of hits returned by vector searches.",
        new HistogramConfiguration { LabelNames = ["engine"], Buckets = [0, 1, 2, 5, 10, 20, 50, 100, 200] });

    internal static SearchTimer Track(string engine) => new(engine);

    internal sealed class SearchTimer(string engine) : IDisposable
    {
        private readonly IDisposable _timer = SearchDuration.WithLabels(engine).NewTimer();
        private int? _hits;
        internal void Success(int hits) => _hits = hits;
        public void Dispose()
        {
            _timer.Dispose();
            Searches.WithLabels(engine, _hits.HasValue ? "success" : "failure").Inc();
            if (_hits.HasValue) SearchHits.WithLabels(engine).Observe(_hits.Value);
        }
    }
}

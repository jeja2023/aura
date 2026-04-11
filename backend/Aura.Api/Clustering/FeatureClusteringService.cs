using Aura.Api.Models;
using System.Text;

namespace Aura.Api.Clustering;

internal sealed record CaptureClusterSource(
    long CaptureId,
    long DeviceId,
    int ChannelNo,
    DateTimeOffset CaptureTime,
    string MetadataJson,
    string? ImagePath);

internal sealed record CaptureClusterPoint(CaptureClusterSource Capture, IReadOnlyList<float> Feature);

internal sealed record ClusterGroup(
    int ClusterIndex,
    IReadOnlyList<CaptureClusterSource> Members,
    long MainDeviceId,
    double CohesionScore);

internal sealed record ClusterResult(
    string Algorithm,
    int CandidateCount,
    int FeatureCount,
    int ClusterCount,
    int NoiseCount,
    IReadOnlyList<ClusterGroup> Groups);

internal sealed class FeatureClusteringService
{
    public ClusterResult ClusterByFeatures(
        IReadOnlyList<CaptureClusterPoint> points,
        double similarityThreshold,
        int minPoints)
    {
        if (points.Count == 0)
        {
            return new ClusterResult("feature-dbscan", 0, 0, 0, 0, []);
        }

        similarityThreshold = Math.Clamp(similarityThreshold, 0.5d, 0.99d);
        minPoints = Math.Max(1, minPoints);

        var vectors = points.Select(x => Normalize(x.Feature)).ToArray();
        var labels = Enumerable.Repeat(Unassigned, points.Count).ToArray();
        var neighbors = BuildNeighbors(vectors, similarityThreshold);

        var clusterId = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (labels[i] != Unassigned)
            {
                continue;
            }

            var region = neighbors[i];
            if (region.Count < minPoints)
            {
                labels[i] = Noise;
                continue;
            }

            clusterId++;
            ExpandCluster(i, region, clusterId, labels, neighbors, minPoints);
        }

        var groups = new List<ClusterGroup>();
        for (var id = 1; id <= clusterId; id++)
        {
            var memberIndexes = Enumerable.Range(0, labels.Length)
                .Where(idx => labels[idx] == id)
                .ToArray();
            if (memberIndexes.Length == 0)
            {
                continue;
            }

            var members = memberIndexes
                .Select(idx => points[idx].Capture)
                .OrderBy(x => x.CaptureTime)
                .ThenBy(x => x.CaptureId)
                .ToArray();
            var dominantDeviceId = members
                .GroupBy(x => x.DeviceId)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key)
                .Select(x => x.Key)
                .First();
            var cohesion = ComputeCohesion(memberIndexes, vectors);
            groups.Add(new ClusterGroup(id, members, dominantDeviceId, cohesion));
        }

        var noiseCount = labels.Count(x => x == Noise);
        return new ClusterResult(
            "feature-dbscan",
            points.Count,
            points.Count,
            groups.Count,
            noiseCount,
            groups);
    }

    public ClusterResult ClusterByTemporalWindow(
        IReadOnlyList<CaptureClusterSource> captures,
        int gapMinutes)
    {
        gapMinutes = gapMinutes <= 0 ? 30 : gapMinutes;
        if (captures.Count == 0)
        {
            return new ClusterResult("temporal-bucket-fallback", 0, 0, 0, 0, []);
        }

        var clusterCounter = 0;
        var groups = captures
            .OrderBy(x => x.CaptureTime)
            .ThenBy(x => x.CaptureId)
            .GroupBy(x => x.DeviceId)
            .SelectMany(g =>
            {
                var buckets = new List<List<CaptureClusterSource>>();
                foreach (var item in g)
                {
                    if (buckets.Count == 0)
                    {
                        buckets.Add([item]);
                        continue;
                    }

                    var current = buckets[^1];
                    var last = current[^1];
                    if ((item.CaptureTime - last.CaptureTime).TotalMinutes <= gapMinutes)
                    {
                        current.Add(item);
                    }
                    else
                    {
                        buckets.Add([item]);
                    }
                }

                return buckets.Select(bucket => new ClusterGroup(
                    ClusterIndex: Interlocked.Increment(ref clusterCounter),
                    Members: bucket
                        .OrderBy(x => x.CaptureTime)
                        .ThenBy(x => x.CaptureId)
                        .ToArray(),
                    MainDeviceId: g.Key,
                    CohesionScore: 0d));
            })
            .ToList();

        return new ClusterResult(
            "temporal-bucket-fallback",
            captures.Count,
            0,
            groups.Count,
            0,
            groups);
    }

    public static string BuildVirtualPersonId(ClusterGroup group)
    {
        var firstId = group.Members.FirstOrDefault()?.CaptureId ?? 0L;
        var payload = $"{group.ClusterIndex}|{firstId}|{group.MainDeviceId}|{group.Members.Count}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return $"V_{hash[..12]}";
    }

    private const int Unassigned = int.MinValue;
    private const int Noise = -1;

    private static List<int>[] BuildNeighbors(IReadOnlyList<float>[] vectors, double similarityThreshold)
    {
        var neighbors = Enumerable.Range(0, vectors.Length).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < vectors.Length; i++)
        {
            for (var j = i; j < vectors.Length; j++)
            {
                var similarity = Cosine(vectors[i], vectors[j]);
                if (similarity < similarityThreshold)
                {
                    continue;
                }

                neighbors[i].Add(j);
                if (i != j)
                {
                    neighbors[j].Add(i);
                }
            }
        }

        return neighbors;
    }

    private static void ExpandCluster(
        int seedIndex,
        List<int> seedNeighbors,
        int clusterId,
        int[] labels,
        List<int>[] neighbors,
        int minPoints)
    {
        labels[seedIndex] = clusterId;
        var queue = new Queue<int>(seedNeighbors);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (labels[current] == Noise)
            {
                labels[current] = clusterId;
            }
            if (labels[current] != Unassigned)
            {
                continue;
            }

            labels[current] = clusterId;
            var currentNeighbors = neighbors[current];
            if (currentNeighbors.Count < minPoints)
            {
                continue;
            }

            foreach (var neighbor in currentNeighbors)
            {
                if (labels[neighbor] == Unassigned || labels[neighbor] == Noise)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }
    }

    private static double ComputeCohesion(IEnumerable<int> indexes, IReadOnlyList<float>[] vectors)
    {
        var members = indexes.ToArray();
        if (members.Length == 0)
        {
            return 0d;
        }

        var dimension = vectors[members[0]].Count;
        var centroid = new float[dimension];
        foreach (var idx in members)
        {
            var vector = vectors[idx];
            for (var i = 0; i < dimension; i++)
            {
                centroid[i] += vector[i];
            }
        }

        for (var i = 0; i < dimension; i++)
        {
            centroid[i] /= members.Length;
        }

        var normalizedCentroid = Normalize(centroid);
        return Math.Round(members.Average(idx => Cosine(vectors[idx], normalizedCentroid)), 4);
    }

    private static IReadOnlyList<float> Normalize(IReadOnlyList<float> source)
    {
        if (source.Count == 0)
        {
            return [];
        }

        double sum = 0d;
        for (var i = 0; i < source.Count; i++)
        {
            sum += source[i] * source[i];
        }

        var norm = Math.Sqrt(sum);
        if (norm <= double.Epsilon)
        {
            return source.ToArray();
        }

        var normalized = new float[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            normalized[i] = (float)(source[i] / norm);
        }

        return normalized;
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var limit = Math.Min(left.Count, right.Count);
        double score = 0d;
        for (var i = 0; i < limit; i++)
        {
            score += left[i] * right[i];
        }
        return score;
    }
}


/* 文件：聚类算法单元测试（ClusteringTests.cs） | File: Clustering algorithm unit tests */
using Aura.Api.Clustering;
using Xunit;

namespace Aura.Api.Tests;

public sealed class ClusteringTests
{
    [Fact]
    public void Feature_DBSCAN_能把相似抓拍聚到同一簇()
    {
        var service = new FeatureClusteringService();
        var captures = new[]
        {
            new CaptureClusterPoint(
                new CaptureClusterSource(1, 1, 1, DateTimeOffset.Parse("2026-04-09T20:00:00+08:00"), "{}", "/tmp/1"),
                [1f, 0f, 0f]),
            new CaptureClusterPoint(
                new CaptureClusterSource(2, 2, 1, DateTimeOffset.Parse("2026-04-09T20:01:00+08:00"), "{}", "/tmp/2"),
                [0.98f, 0.02f, 0f]),
            new CaptureClusterPoint(
                new CaptureClusterSource(3, 3, 1, DateTimeOffset.Parse("2026-04-09T20:02:00+08:00"), "{}", "/tmp/3"),
                [0f, 1f, 0f]),
        };

        var result = service.ClusterByFeatures(captures, similarityThreshold: 0.9d, minPoints: 2);
        Assert.Equal(1, result.ClusterCount);
        Assert.Equal(1, result.NoiseCount);
        Assert.Equal(2, result.Groups[0].Members.Count);
    }

    [Fact]
    public void 时间窗口回退能保持每设备分桶()
    {
        var service = new FeatureClusteringService();
        var captures = new[]
        {
            new CaptureClusterSource(10, 1, 1, DateTimeOffset.Parse("2026-04-09T20:00:00+08:00"), "{}", null),
            new CaptureClusterSource(11, 1, 1, DateTimeOffset.Parse("2026-04-09T20:10:00+08:00"), "{}", null),
            new CaptureClusterSource(12, 1, 1, DateTimeOffset.Parse("2026-04-09T21:00:00+08:00"), "{}", null),
        };

        var result = service.ClusterByTemporalWindow(captures, gapMinutes: 30);
        Assert.Equal(2, result.ClusterCount);
    }
}


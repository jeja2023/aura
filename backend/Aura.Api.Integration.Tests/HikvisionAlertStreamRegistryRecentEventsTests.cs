/* 文件：海康告警流最近事件缓存单元测试 | File: Hikvision alert stream recent events cache tests */
using Aura.Api.Services.Hikvision;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class HikvisionAlertStreamRegistryRecentEventsTests
{
    [Fact]
    public void TryFindRecentEvent_窗口内优先选择最新且带通道号()
    {
        var reg = new HikvisionAlertStreamRegistry();
        var now = DateTimeOffset.UtcNow;

        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "A", "active", null, "<x/>", now.AddSeconds(-2)));
        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "B", "active", 3, "<y/>", now.AddSeconds(-1)));

        var picked = reg.TryFindRecentEvent(1, now, maxAgeSeconds: 10, maxCacheSize: 32, requireChannelNo: true);
        Assert.NotNull(picked);
        Assert.Equal(3, picked!.ChannelNo);
        Assert.Equal("B", picked.EventType);
    }

    [Fact]
    public void TryFindRecentEvent_窗口外事件会被淘汰()
    {
        var reg = new HikvisionAlertStreamRegistry();
        var now = DateTimeOffset.UtcNow;

        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "A", "active", 1, "<x/>", now.AddSeconds(-100)));
        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "B", "active", 2, "<y/>", now.AddSeconds(-1)));

        var picked = reg.TryFindRecentEvent(1, now, maxAgeSeconds: 10, maxCacheSize: 32, requireChannelNo: true);
        Assert.NotNull(picked);
        Assert.Equal(2, picked!.ChannelNo);
        Assert.Equal("B", picked.EventType);
    }

    [Fact]
    public void TryFindRecentEvent_当要求通道号但均缺失_返回空()
    {
        var reg = new HikvisionAlertStreamRegistry();
        var now = DateTimeOffset.UtcNow;

        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "A", "active", null, "<x/>", now.AddSeconds(-1)));
        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "B", "active", 0, "<y/>", now));

        var picked = reg.TryFindRecentEvent(1, now, maxAgeSeconds: 10, maxCacheSize: 32, requireChannelNo: true);
        Assert.Null(picked);
    }

    [Fact]
    public void TryFindRecentEvent_不要求通道号时可返回最近一条()
    {
        var reg = new HikvisionAlertStreamRegistry();
        var now = DateTimeOffset.UtcNow;

        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "A", "active", null, "<x/>", now.AddSeconds(-1)));
        reg.SetLastEvent(1, new HikvisionAlertStreamEventSnap("EventNotificationAlert", "B", "active", null, "<y/>", now));

        var picked = reg.TryFindRecentEvent(1, now, maxAgeSeconds: 10, maxCacheSize: 32, requireChannelNo: false);
        Assert.NotNull(picked);
        Assert.Equal("B", picked!.EventType);
    }
}


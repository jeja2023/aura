using Aura.Api.Data;
using Xunit;

namespace Aura.Api.Tests;

public sealed class DapperTypeHandlerTests
{
    [Fact]
    public void DateTimeOffsetHandler_MapsNpgsqlUtcDateTime()
    {
        var utc = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

        var result = new DapperTypeHandlers.DateTimeOffsetHandler().Parse(utc);

        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(utc, result.UtcDateTime);
    }
}

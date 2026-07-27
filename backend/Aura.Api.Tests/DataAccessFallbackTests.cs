using Aura.Api.Data;
using Xunit;

namespace Aura.Api.Tests;

public sealed class DataAccessFallbackTests
{
    [Fact]
    public void 已配置数据库时仓储异常必须上抛()
    {
        var factory = new PgSqlConnectionFactory("Host=localhost;Database=aura;Username=test;Password=test");

        var exception = Assert.Throws<DataAccessUnavailableException>(() =>
            PgSqlRepositoryHelpers.ThrowIfConfigured(factory, new InvalidOperationException("failed"), "test operation"));

        Assert.Contains("test operation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未配置数据库时允许显式开发回退()
    {
        var factory = new PgSqlConnectionFactory(string.Empty);

        PgSqlRepositoryHelpers.ThrowIfConfigured(factory, new InvalidOperationException("failed"), "test operation");
    }
}

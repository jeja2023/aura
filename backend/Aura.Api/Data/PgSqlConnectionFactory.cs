using Npgsql;

namespace Aura.Api.Data;

internal sealed class PgSqlConnectionFactory
{
    private readonly string _connectionString;

    public PgSqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString ?? string.Empty;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public NpgsqlConnection CreateConnection() => new(_connectionString);
}

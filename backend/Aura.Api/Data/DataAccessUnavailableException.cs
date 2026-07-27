namespace Aura.Api.Data;

internal sealed class DataAccessUnavailableException(string operation, Exception innerException)
    : Exception($"PostgreSQL operation failed: {operation}", innerException);

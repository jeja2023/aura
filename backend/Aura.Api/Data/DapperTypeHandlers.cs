using System.Data;
using Dapper;

namespace Aura.Api.Data;

internal static class DapperTypeHandlers
{
    private static int registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) != 0) return;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    internal sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.UtcDateTime;

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset.ToUniversalTime(),
            DateTime dateTime when dateTime.Kind == DateTimeKind.Utc => new DateTimeOffset(dateTime),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot convert {value.GetType().Name} to DateTimeOffset")
        };
    }
}

using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ledgerline.Infrastructure.ReadModel;

/// <summary>
/// Makes Dapper read and write the same representations EF Core uses, on both providers:
/// PostgreSQL has native <c>uuid</c> and <c>timestamptz</c>; EF Core's SQLite provider stores both as TEXT
/// (upper-case GUIDs, ISO-8601 timestamps with offset).
/// </summary>
internal static class DapperSetup
{
    private static int _configured;

    public static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Dapper maps Guid and DateTimeOffset natively; removing the built-in mapping lets the handlers run.
        SqlMapper.RemoveTypeMap(typeof(Guid));
        SqlMapper.RemoveTypeMap(typeof(Guid?));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
        SqlMapper.AddTypeHandler(new GuidHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            if (parameter is SqliteParameter)
            {
                parameter.DbType = DbType.String;
                parameter.Value = value.ToString().ToUpperInvariant();
                return;
            }

            parameter.Value = value;
        }

        public override Guid Parse(object value) => value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text, CultureInfo.InvariantCulture),
            byte[] bytes => new Guid(bytes),
            _ => throw new DataException($"Cannot convert {value.GetType().Name} to Guid."),
        };
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            if (parameter is SqliteParameter)
            {
                parameter.DbType = DbType.String;
                parameter.Value = value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
                return;
            }

            parameter.Value = value.ToUniversalTime();
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            DateTime utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => throw new DataException($"Cannot convert {value.GetType().Name} to DateTimeOffset."),
        };
    }
}

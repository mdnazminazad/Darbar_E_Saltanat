using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DarbarESaltanat.Infrastructure.Persistence;

/// <summary>Applies WAL + safety PRAGMAs to every connection EF opens.</summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Apply(DbConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }
}

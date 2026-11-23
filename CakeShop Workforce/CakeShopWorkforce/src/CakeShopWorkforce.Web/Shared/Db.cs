using Npgsql;

namespace CakeShopWorkforce.Web.Shared;

/// <summary>
/// Gives every feature an open PostgreSQL connection.
///
/// Usage inside a handler:
/// <code>
///   await using var conn = await db.OpenAsync();
///   var rows = await conn.QueryAsync&lt;MyRow&gt;("select ...", new { ... });
/// </code>
/// "await using" closes the connection automatically (it goes back to the pool).
/// Keep connections short: open, run your SQL, done.
/// </summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    public async Task<NpgsqlConnection> OpenAsync() => await dataSource.OpenConnectionAsync();
}

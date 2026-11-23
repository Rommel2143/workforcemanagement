using System.Reflection;
using CakeShopWorkforce.Web.Features.Auth;
using CakeShopWorkforce.Web.Shared;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace CakeShopWorkforce.Web.Database;

/// <summary>
/// Runs when the app starts:
///   1. Creates the database if it does not exist yet.
///   2. Runs every SQL script in Database/Scripts that has not run before.
///   3. Creates the first Owner login (from appsettings.json → DefaultAdmin) if there are no users.
///
/// You never have to run SQL by hand — just start the app.
/// </summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var connectionString = config.GetConnectionString("CakeShop")
            ?? throw new InvalidOperationException("Missing connection string 'CakeShop' in appsettings.json");

        await CreateDatabaseIfMissingAsync(connectionString, logger);

        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await dataSource.OpenConnectionAsync();

        // Table that remembers which scripts already ran
        await conn.ExecuteAsync("""
            create table if not exists schema_versions (
                script_name text primary key,
                applied_at  timestamptz not null default now()
            )
            """);

        var applied = (await conn.QueryAsync<string>("select script_name from schema_versions")).ToHashSet();

        foreach (var (name, sql) in ReadEmbeddedScripts())
        {
            if (applied.Contains(name)) continue;

            logger.LogInformation("Running database script {Script}", name);
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("insert into schema_versions (script_name) values (@name)", new { name }, tx);
            await tx.CommitAsync();
        }

        await EnsureDefaultAdminAsync(conn, config, logger);
    }

    /// <summary>Scripts are embedded in the app (see the .csproj). Sorted by name: 001, 002, ...</summary>
    private static IEnumerable<(string Name, string Sql)> ReadEmbeddedScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "CakeShopWorkforce.Web.Database.Scripts.";

        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[prefix.Length..], reader.ReadToEnd());
        }
    }

    private static async Task CreateDatabaseIfMissingAsync(string connectionString, ILogger logger)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = builder.Database!;
        builder.Database = "postgres";   // connect to the default database to check / create ours

        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        var exists = await conn.ExecuteScalarAsync<bool>(
            "select exists(select 1 from pg_database where datname = @databaseName)", new { databaseName });

        if (!exists)
        {
            logger.LogInformation("Creating database {Database}", databaseName);
            // Database names cannot be SQL parameters, so we quote the name safely
            await conn.ExecuteAsync($"create database \"{databaseName.Replace("\"", "\"\"")}\"");
        }
    }

    private static async Task EnsureDefaultAdminAsync(NpgsqlConnection conn, IConfiguration config, ILogger logger)
    {
        var hasUsers = await conn.ExecuteScalarAsync<bool>("select exists(select 1 from users)");
        if (hasUsers) return;

        var userName = config["DefaultAdmin:UserName"] ?? "admin";
        var password = config["DefaultAdmin:Password"] ?? "Admin@12345";
        var displayName = config["DefaultAdmin:DisplayName"] ?? "Store Owner";

        var hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), password);
        await conn.ExecuteAsync("""
            insert into users (user_name, display_name, password_hash, role)
            values (@userName, @displayName, @hash, @role)
            """, new { userName, displayName, hash, role = Roles.Owner });

        logger.LogWarning("Created the first Owner login '{User}'. Sign in and change the password in Settings → Users.", userName);
    }
}

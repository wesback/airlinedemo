using System.Text.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Persistence;
using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Deployment;

public static class Program
{
    public static Task Main(string[] args) => SqlDeployment.RunAsync(args);
}

public static class SqlDeployment
{
    public static string ContainedUserSql(string name, Guid clientId)
    {
        if (!Regex.IsMatch(name, @"^id-airline-(app|migration)$", RegexOptions.CultureInvariant) ||
            clientId == Guid.Empty)
            throw new ArgumentException("Expected a deployment identity name and nonempty client ID.");
        var sid = "0x" + Convert.ToHexString(clientId.ToByteArray());
        return $"IF DATABASE_PRINCIPAL_ID('{name}') IS NULL CREATE USER [{name}] WITH SID={sid}, TYPE=E; " +
            $"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name=N'{name}' AND sid={sid}) " +
            "THROW 51000, 'Existing SQL identity SID does not match the managed identity client ID', 1;";
    }

    public static async Task WithTemporaryInsertAsync(
        Func<string, Task> execute, Func<Task> migrate)
    {
        const string user = "[id-airline-migration]";
        await execute($"GRANT INSERT ON SCHEMA::dbo TO {user};");
        try
        {
            await migrate();
        }
        finally
        {
            await execute($"REVOKE INSERT ON SCHEMA::dbo FROM {user};");
        }
    }

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: AirlineDemo.Deployment <protected configuration JSON>");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
        var root = config.RootElement;
        var server = root.GetProperty("server").GetString()!;
        var database = root.GetProperty("database").GetString()!;
        var appId = root.GetProperty("appClientId").GetGuid();
        var migrationId = root.GetProperty("migrationClientId").GetGuid();
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{server},1433", InitialCatalog = database,
            Encrypt = true, TrustServerCertificate = false, ConnectTimeout = 30,
        }.ConnectionString;

        async Task<SqlConnection> OpenAsync(TokenCredential credential)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://database.windows.net/.default"]), default);
            var connection = new SqlConnection(connectionString) { AccessToken = token.Token };
            await connection.OpenAsync();
            return connection;
        }

        await using var admin = await OpenAsync(new AzureCliCredential());
        async Task Execute(string sql)
        {
            await using var command = new SqlCommand(sql, admin) { CommandTimeout = 180 };
            await command.ExecuteNonQueryAsync();
        }

        await Execute("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = 'airlinedemo-schema-bootstrap',
              @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
            IF @result < 0 THROW 51001, 'Another SQL bootstrap is running', 1;
            """);
        await Execute("""
            IF SCHEMA_ID('audit') IS NULL EXEC('CREATE SCHEMA audit');
            IF OBJECT_ID('dbo.__EFMigrationsHistory') IS NULL
            CREATE TABLE dbo.__EFMigrationsHistory
              (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL);
            """);
        await Execute(ContainedUserSql("id-airline-app", appId));
        await Execute(ContainedUserSql("id-airline-migration", migrationId));
        foreach (var statement in root.GetProperty("migrationGrants").EnumerateArray())
            await Execute(statement.GetString()!);

        await WithTemporaryInsertAsync(Execute, async () =>
        {
            await using var connection = await OpenAsync(
                new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(migrationId.ToString())));
            var options = new DbContextOptionsBuilder<CaseStateDbContext>()
                .UseSqlServer(connection).Options;
            await using var db = new CaseStateDbContext(options);
            await db.Database.MigrateAsync();
            var expected = db.Database.GetMigrations().ToArray();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            if (!expected.SequenceEqual(applied))
                throw new InvalidOperationException("Applied migrations do not match the repository migration chain.");
            Console.WriteLine($"Verified {applied.Length} migrations; head={applied[^1]}");
        });

        foreach (var statement in root.GetProperty("appGrants").EnumerateArray())
            await Execute(statement.GetString()!);
        const string check = """
            EXECUTE AS USER = 'id-airline-app';
            SELECT HAS_PERMS_BY_NAME('dbo.cases', 'OBJECT', 'SELECT'),
              HAS_PERMS_BY_NAME('dbo.cases', 'OBJECT', 'UPDATE'),
              HAS_PERMS_BY_NAME('audit.case_events', 'OBJECT', 'SELECT'),
              HAS_PERMS_BY_NAME('audit.case_events', 'OBJECT', 'INSERT'),
              HAS_PERMS_BY_NAME('audit.case_events', 'OBJECT', 'UPDATE'),
              HAS_PERMS_BY_NAME('audit.case_events', 'OBJECT', 'DELETE'),
              HAS_PERMS_BY_NAME('audit.case_events', 'OBJECT', 'ALTER'),
              HAS_PERMS_BY_NAME('audit.case_events', 'OBJECT', 'CONTROL'),
              HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'),
              HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'ALTER');
            REVERT;
            """;
        await using var verify = new SqlCommand(check, admin);
        await using var reader = await verify.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Missing SQL permission results.");
        int[] permissions = [1, 1, 1, 1, 0, 0, 0, 0, 0, 0];
        for (var index = 0; index < permissions.Length; index++)
            if (reader.IsDBNull(index) || reader.GetInt32(index) != permissions[index])
                throw new InvalidOperationException($"Unexpected application SQL permission at index {index}.");
        Console.WriteLine("Verified application read/write and append-only audit permissions; no application DDL.");
    }
}

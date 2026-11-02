using AirlineDemo.Deployment;

namespace AirlineDemo.Api.Tests;

public sealed class SqlDeploymentTests
{
    [Fact]
    public void Contained_identity_uses_client_guid_byte_order_and_checks_existing_sid()
    {
        Assert.Equal(
            "IF DATABASE_PRINCIPAL_ID('id-airline-app') IS NULL CREATE USER [id-airline-app] WITH SID=0x11111111222233438444555555555555, TYPE=E; " +
            "IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name=N'id-airline-app' AND sid=0x11111111222233438444555555555555) " +
            "THROW 51000, 'Existing SQL identity SID does not match the managed identity client ID', 1;",
            SqlDeployment.ContainedUserSql("id-airline-app", Guid.Parse("11111111-2222-4333-8444-555555555555")));
    }

    [Theory]
    [InlineData("dbo", "11111111-2222-4333-8444-555555555555")]
    [InlineData("id-airline-app]; DROP USER dbo;--", "11111111-2222-4333-8444-555555555555")]
    [InlineData("id-airline-app", "00000000-0000-0000-0000-000000000000")]
    public void Invalid_identity_is_rejected(string name, string clientId)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SqlDeployment.ContainedUserSql(name, Guid.Parse(clientId)));
        Assert.Equal("Expected a deployment identity name and nonempty client ID.", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Temporary_insert_is_revoked_after_success_or_migration_failure(bool fail)
    {
        List<string> calls = [];
        Task Execute(string sql) { calls.Add(sql); return Task.CompletedTask; }
        Task Migrate()
        {
            calls.Add("migrate");
            return fail ? Task.FromException(new InvalidOperationException("migration failed")) : Task.CompletedTask;
        }
        if (fail)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqlDeployment.WithTemporaryInsertAsync(Execute, Migrate));
            Assert.Equal("migration failed", error.Message);
        }
        else
            await SqlDeployment.WithTemporaryInsertAsync(Execute, Migrate);
        Assert.Equal([
            "GRANT INSERT ON SCHEMA::dbo TO [id-airline-migration];",
            "migrate",
            "REVOKE INSERT ON SCHEMA::dbo FROM [id-airline-migration];",
        ], calls);
    }
}

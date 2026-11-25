using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Infrastructure;

[Trait("Category", "Unit")]
public class SqliteWebAppFactoryBaseTests(AuthenticationTestFactory factory)
    : IClassFixture<AuthenticationTestFactory>
{
    [Fact]
    public async Task AContextReadsWhileAnotherContextHoldsAnOpenStatement()
    {
        var pool = factory.Services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        await using var reading = await pool.CreateDbContextAsync();
        await reading.Database.OpenConnectionAsync();
        await using var command = reading.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id FROM tenants UNION ALL SELECT id FROM subjects";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        await using var other = await pool.CreateDbContextAsync();

        (await other.Tenants.CountAsync()).Should().Be(1);
    }
}

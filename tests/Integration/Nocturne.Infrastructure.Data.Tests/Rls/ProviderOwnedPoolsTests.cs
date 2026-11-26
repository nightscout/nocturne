using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The pools <see cref="ServiceCollectionExtensions.AddPostgreSqlInfrastructure(IServiceCollection, string, Microsoft.Extensions.Configuration.IConfiguration, Action{Configuration.PostgreSqlConfiguration})"/>
/// registers belong to the provider built from it. A test that builds a provider per test would
/// otherwise leave every pool's idle connections on the server for their whole idle lifetime, and
/// a suite of them exhausts <c>max_connections</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ProviderOwnedPoolsTests(RlsCompletenessFixture fx)
{
    [Fact]
    public async Task Disposing_the_provider_closes_both_pools_connections()
    {
        var applicationName = $"provider_owned_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fx.AppConnectionString)
        {
            ApplicationName = applicationName,
            // Longer than the test, so only the dispose can close the pools' connections.
            ConnectionIdleLifetime = 3600,
        }.ConnectionString;

        await using var provider = TestContexts.BuildProvider(connectionString);

        await using (var context = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync())
        {
            await context.Database.OpenConnectionAsync();
        }

        var raw = await provider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await raw.DisposeAsync();

        (await BackendsAsync(applicationName)).Should().Be(2, "each pool keeps its idle connection while the provider lives");

        await provider.DisposeAsync();

        var deadline = DateTime.UtcNow.AddSeconds(3);
        long remaining;
        while ((remaining = await BackendsAsync(applicationName)) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        remaining.Should().Be(0, "a disposed provider must not leave its pools' connections open on the server");
    }

    private async Task<long> BackendsAsync(string applicationName)
    {
        await using var probe = new NpgsqlConnection(fx.AppConnectionString + ";Pooling=false");
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name", probe);
        cmd.Parameters.AddWithValue("name", applicationName);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

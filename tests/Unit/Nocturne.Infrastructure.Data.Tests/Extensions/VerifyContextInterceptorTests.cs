using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// The startup check that <see cref="NocturneDbContext"/> opens the context pool only through
/// <see cref="TenantConnectionInterceptor"/>: that pool skips the session reset on pool return, so a
/// context opening it without the interceptor would read the previous lessee's RLS session state.
/// </summary>
[Trait("Category", "Unit")]
public class VerifyContextInterceptorTests
{
    private const string ConnectionString = "Host=localhost;Database=nocturne;Username=nocturne_app;Password=secret";

    [Fact]
    public async Task AcceptsTheRegisteredContextOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlInfrastructure(ConnectionString, configuration: null);
        await using var provider = services.BuildServiceProvider();
        await using var context = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>()
            .CreateDbContextAsync();
        var contexts = provider.GetRequiredService<ContextDataSource>().Source;

        var act = () => DatabaseInitializationExtensions.VerifyContextInterceptor(
            context.GetService<IDbContextOptions>(), contexts);

        act.Should().NotThrow();
    }

    [Fact]
    public void RejectsOptionsWithoutTheInterceptor()
    {
        using var contexts = NpgsqlDataSource.Create(ConnectionString);
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(contexts)
            .AddInterceptors(new PreparedStatementRepairInterceptor())
            .Options;

        var act = () => DatabaseInitializationExtensions.VerifyContextInterceptor(options, contexts);

        act.Should().Throw<InvalidOperationException>().WithMessage("*without TenantConnectionInterceptor*");
    }

    [Fact]
    public void RejectsOptionsOnAnotherPool()
    {
        using var contexts = NpgsqlDataSource.Create(ConnectionString);
        using var other = NpgsqlDataSource.Create(ConnectionString);
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(other)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        var act = () => DatabaseInitializationExtensions.VerifyContextInterceptor(options, contexts);

        act.Should().Throw<InvalidOperationException>().WithMessage("*does not open the registered context pool*");
    }
}

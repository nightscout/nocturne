using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public sealed class GoogleHealthGrantHoldersTests : IDisposable
{
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();
    private readonly SqliteTestDatabase database = TestDbContextFactory.CreateSqliteWithTenant(First, "first")
        .SeedTenant(Second, "second");
    private readonly Dictionary<Guid, Func<Dictionary<string, string>>> secrets = [];
    private readonly ServiceProvider provider;

    public GoogleHealthGrantHoldersTests()
    {
        var services = new ServiceCollection();
        database.AddToServices(services);
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped(scope =>
        {
            var tenant = scope.GetRequiredService<ITenantAccessor>().TenantId;
            var configurations = new Mock<IConnectorConfigurationService>();
            configurations.Setup(value => value.GetSecretsAsync("GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => secrets.TryGetValue(tenant, out var read) ? read() : []);
            return configurations.Object;
        });
        provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        provider.Dispose();
        database.Dispose();
    }

    private PostgresGoogleHealthGrantHolders Holders() => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        provider.GetRequiredService<INocturneDbContextFactory>(),
        new TenantRepository(provider.GetRequiredService<INocturneDbContextFactory>()),
        NullLogger<PostgresGoogleHealthGrantHolders>.Instance);

    private static Func<Dictionary<string, string>> Session(string account) =>
        () => new() { ["refreshToken"] = "refresh", ["accountKey"] = account };

    [Fact]
    public async Task An_account_is_shared_only_while_another_tenant_holds_it()
    {
        var holders = Holders();
        await holders.HoldAsync(First, "account", default);
        await holders.HoldAsync(Second, "account", default);

        Assert.Equal(GoogleHealthGrantSharing.Shared, await holders.SharingAsync(First, "account", default));
        Assert.Equal(GoogleHealthGrantSharing.NotShared, await holders.SharingAsync(First, "other", default));

        await holders.ReleaseAsync(Second, default);

        Assert.Equal(GoogleHealthGrantSharing.NotShared, await holders.SharingAsync(First, "account", default));
    }

    [Fact]
    public async Task Holding_another_account_replaces_the_tenants_hold()
    {
        var holders = Holders();
        await holders.HoldAsync(Second, "former", default);

        await holders.HoldAsync(Second, "current", default);

        Assert.Equal(GoogleHealthGrantSharing.NotShared, await holders.SharingAsync(First, "former", default));
        Assert.Equal(GoogleHealthGrantSharing.Shared, await holders.SharingAsync(First, "current", default));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.GoogleHealthGrantHolders.CountAsync(row => row.TenantId == Second));
    }

    [Fact]
    public async Task A_token_stored_before_its_tenant_was_recorded_is_adopted()
    {
        secrets[Second] = Session("legacy");

        Assert.Equal(GoogleHealthGrantSharing.Shared, await Holders().SharingAsync(First, "legacy", default));
    }

    [Fact]
    public async Task A_recorded_release_is_never_undone_by_adoption()
    {
        var holders = Holders();
        await holders.ReleaseAsync(Second, default);
        secrets[Second] = Session("released");

        Assert.Equal(GoogleHealthGrantSharing.NotShared, await holders.SharingAsync(First, "released", default));
    }

    [Fact]
    public async Task An_unreadable_tenant_is_skipped_and_adoption_runs_once()
    {
        var reads = 0;
        secrets[First] = () =>
        {
            reads++;
            throw new JsonException("corrupt");
        };
        secrets[Second] = Session("readable");
        var holders = Holders();

        Assert.Equal(GoogleHealthGrantSharing.Shared, await holders.SharingAsync(First, "readable", default));
        Assert.Equal(GoogleHealthGrantSharing.Shared, await holders.SharingAsync(First, "readable", default));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_tenant_with_no_session_is_not_adopted()
    {
        secrets[Second] = () => new() { ["accountKey"] = "disconnected" };

        Assert.Equal(GoogleHealthGrantSharing.NotShared, await Holders().SharingAsync(First, "disconnected", default));
    }

    [Fact]
    public async Task Holding_for_a_tenant_that_does_not_exist_fails_rather_than_dropping_the_hold()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Holders().HoldAsync(Guid.NewGuid(), "account", default));
    }
}

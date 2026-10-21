using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Xunit;

namespace Nocturne.API.Integration.Tests.Health;

[Trait("Category", "Integration")]
public sealed class GoogleHealthGrantHoldersTests(GoogleHealthPostgresFixture fixture)
    : IClassFixture<GoogleHealthPostgresFixture>, IAsyncLifetime, IDisposable
{
    private readonly Guid first = Guid.NewGuid();
    private readonly Guid second = Guid.NewGuid();
    private readonly Dictionary<Guid, Dictionary<string, string>> secrets = [];
    private readonly List<string> revoked = [];
    private readonly List<IDisposable> disposables = [];
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        await using (var db = SuperuserContext())
        {
            db.Tenants.AddRange(
                new TenantEntity { Id = first, Slug = $"grant-first-{first:N}", DisplayName = "Synthetic first", IsActive = true },
                new TenantEntity { Id = second, Slug = $"grant-second-{second:N}", DisplayName = "Synthetic second", IsActive = false });
            await db.SaveChangesAsync();
        }
        provider = Services();
    }

    public async Task DisposeAsync() => await provider.DisposeAsync();

    public void Dispose() => disposables.ForEach(disposable => disposable.Dispose());

    [Fact]
    public async Task Two_tenants_on_one_google_account_revoke_its_grant_only_when_the_last_one_leaves()
    {
        var holders = Holders();
        var firstSessions = Sessions(first, holders);
        var secondSessions = Sessions(second, holders);
        await StoreAsync(first, firstSessions, "refresh-first", "shared-account");
        await StoreAsync(second, secondSessions, "refresh-second", "shared-account");

        await ClearAsync(second, secondSessions);

        Assert.Empty(revoked);
        Assert.Equal("refresh-first", secrets[first]["refreshToken"]);
        Assert.Equal([first], await HolderTenantsAsync("shared-account"));

        await ClearAsync(first, firstSessions);

        Assert.Equal(["token=refresh-first"], revoked);
        Assert.Empty(await HolderTenantsAsync("shared-account"));
    }

    [Fact]
    public async Task Another_account_does_not_keep_a_grant()
    {
        var holders = Holders();
        await holders.HoldAsync(first, "account-a", default);

        Assert.False(await holders.MayBeHeldByAnotherTenantAsync(second, "account-b", default));
        Assert.False(await holders.MayBeHeldByAnotherTenantAsync(first, "account-a", default));
        Assert.True(await holders.MayBeHeldByAnotherTenantAsync(second, "account-a", default));
    }

    [Fact]
    public async Task A_token_stored_before_its_tenant_was_recorded_as_a_holder_keeps_the_grant()
    {
        secrets[first] = new() { ["refreshToken"] = "refresh-first", ["accountKey"] = "legacy-account" };
        secrets[second] = new() { ["accountKey"] = "disconnected-account" };

        var holders = Holders();

        Assert.True(await holders.MayBeHeldByAnotherTenantAsync(second, "legacy-account", default));
        Assert.False(await holders.MayBeHeldByAnotherTenantAsync(first, "disconnected-account", default));
    }

    [Fact]
    public async Task Deleting_a_tenant_drops_its_hold()
    {
        var holders = Holders();
        await holders.HoldAsync(first, "deleted-account", default);

        await using (var db = SuperuserContext())
            await db.Tenants.Where(tenant => tenant.Id == first).ExecuteDeleteAsync();

        Assert.False(await holders.MayBeHeldByAnotherTenantAsync(second, "deleted-account", default));
    }

    private PostgresGoogleHealthGrantHolders Holders() =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PostgresGoogleHealthGrantHolders>.Instance);

    private async Task StoreAsync(Guid tenant, GoogleHealthSessionStore sessions, string refreshToken, string account)
    {
        secrets[tenant] = [];
        await sessions.StoreAsync(new GoogleHealthTokenSession(refreshToken, ["scope"]), account, Guid.NewGuid(), default);
    }

    private static Task ClearAsync(Guid tenant, GoogleHealthSessionStore sessions) =>
        sessions.ClearAsync(removeAccount: false, Guid.NewGuid(), revoke: true, default);

    private async Task<Guid[]> HolderTenantsAsync(string account)
    {
        await using var db = SuperuserContext();
        return await db.GoogleHealthGrantHolders.Where(row => row.AccountKey == account)
            .Select(row => row.TenantId).ToArrayAsync();
    }

    /// <summary>A tenant's session store over its own secrets, recording what it revokes at Google.</summary>
    private GoogleHealthSessionStore Sessions(Guid tenant, PostgresGoogleHealthGrantHolders holders)
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(value => value.IsResolved).Returns(true);
        accessor.SetupGet(value => value.TenantId).Returns(tenant);
        var platform = Options.Create(new GoogleHealthPlatformOptions
        {
            ClientId = "synthetic.apps.googleusercontent.com",
            ClientSecret = "synthetic-secret",
            RedirectUri = "https://example.test" + GoogleHealthPlatformOptions.CallbackPath
        });
        var http = new HttpClient(new RevokeRecorder(revoked));
        disposables.Add(http);
        var oauth = new GoogleHealthAuthTokenProvider(http, new ConnectorTokenCache(),
            new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
            accessor.Object, platform, NullLogger<GoogleHealthAuthTokenProvider>.Instance);
        return new GoogleHealthSessionStore(SecretsOf(tenant), oauth, holders, accessor.Object, platform);
    }

    private IConnectorConfigurationService SecretsOf(Guid tenant)
    {
        var configurations = new Mock<IConnectorConfigurationService>();
        configurations.Setup(value => value.GetSecretsAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<string, string>(
                secrets.GetValueOrDefault(tenant) ?? [], StringComparer.OrdinalIgnoreCase));
        configurations.Setup(value => value.SaveSecretsAsync(
                "GoogleHealth", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, string>, string?, CancellationToken>(
                (_, values, _, _) => secrets[tenant] = new(values, StringComparer.OrdinalIgnoreCase))
            .Returns(Task.CompletedTask);
        return configurations.Object;
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(fixture.Database.AppConnectionString));
        services.AddDbContextFactory<NocturneDbContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()).AddInterceptors(new TenantConnectionInterceptor()));
        services.AddDataServices();
        services.AddScoped(scope => SecretsOf(scope.GetRequiredService<ITenantAccessor>().TenantId));
        return services.BuildServiceProvider();
    }

    private NocturneDbContext SuperuserContext() => new(new DbContextOptionsBuilder<NocturneDbContext>()
        .UseNpgsql(fixture.Database.SuperuserConnectionString).Options);

    private sealed class RevokeRecorder(List<string> revoked) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/revoke", request.RequestUri!.AbsolutePath);
            revoked.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}

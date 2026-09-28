using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Exercises <see cref="OidcProviderService.SyncConfigProvidersAsync"/> against a real
/// EF InMemory <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
public class OidcProviderServiceSyncTests
{
    private static ServiceProvider BuildServices(string databaseName, params OidcProviderConfig[] providers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<OidcOptions>(options =>
        {
            foreach (var provider in providers)
            {
                options.Providers.Add(provider);
            }
        });
        services.AddDbContextFactory<NocturneDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private static OidcProviderConfig Config(string issuer) => new()
    {
        Name = issuer,
        IssuerUrl = issuer,
        ClientId = "nocturne",
    };

    private static async Task SeedProviderAsync(
        IDbContextFactory<NocturneDbContext> factory, string issuer, bool isEnabled)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = OidcProviderService.CreateDeterministicGuid(issuer),
            Name = issuer,
            IssuerUrl = issuer,
            ClientId = "nocturne",
            IsEnabled = isEnabled,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SyncConfigProvidersAsync_DisablesDbProviderAbsentFromConfig()
    {
        var databaseName = $"OidcSync_{Guid.NewGuid()}";
        var configuredIssuer = "https://a.example";
        var absentIssuer = "https://b.example";
        using var services = BuildServices(databaseName, Config(configuredIssuer));
        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await SeedProviderAsync(factory, absentIssuer, isEnabled: true);

        await OidcProviderService.SyncConfigProvidersAsync(services);

        await using var db = await factory.CreateDbContextAsync();
        var configured = await db.OidcProviders.SingleAsync(p => p.IssuerUrl == configuredIssuer);
        configured.IsEnabled.Should().BeTrue();
        var absent = await db.OidcProviders.SingleAsync(p => p.IssuerUrl == absentIssuer);
        absent.IsEnabled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SyncConfigProvidersAsync_WithNoConfiguredProviders_LeavesDbProvidersEnabled()
    {
        var databaseName = $"OidcSync_{Guid.NewGuid()}";
        var dbManagedIssuer = "https://b.example";
        using var services = BuildServices(databaseName);
        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await SeedProviderAsync(factory, dbManagedIssuer, isEnabled: true);

        await OidcProviderService.SyncConfigProvidersAsync(services);

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.OidcProviders.SingleAsync(p => p.IssuerUrl == dbManagedIssuer);
        row.IsEnabled.Should().BeTrue();
    }
}

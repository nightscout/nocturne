using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Characterizes what the connector effective-configuration read returns. The account identifier
/// (username/email) is not a <c>Secret</c> property, so it is included in the effective values;
/// that is why the endpoint returning them must require authentication
/// (<see cref="Controllers.V4.Connectors.ConfigurationControllerAttributeTests"/>). Passwords and
/// other secrets are excluded here regardless of the transport gate.
/// </summary>
public class ConnectorEffectiveConfigurationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;

    public ConnectorEffectiveConfigurationTests()
    {
        // Force-load the Glooko connector assembly so the service's reflection-based type lookup
        // (AppDomain scan for [ConnectorRegistration]) can resolve "Glooko".
        _ = typeof(GlookoConnectorConfiguration);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(dbOptions) { TenantId = Guid.CreateVersion7() };
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private ConnectorConfigurationService CreateService(IConfiguration configuration) =>
        new(
            _dbContext,
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            configuration,
            Mock.Of<IHostEnvironment>(),
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            NullLogger<ConnectorConfigurationService>.Instance);

    [Fact]
    public async Task GetEffectiveConfiguration_IncludesAccountIdentifier_ExcludesSecret()
    {
        // A connector configured with an account identifier and a secret. The account email is a
        // non-secret field, so a filter that did nothing would return it — this seeds the value the
        // filter and the authorization gate must handle.
        const string accountEmail = "connector-user@example.test";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connectors:Glooko:Email"] = accountEmail,
                ["Connectors:Glooko:Password"] = "connector-secret",
            })
            .Build();

        var service = CreateService(configuration);

        var effective = await service.GetEffectiveConfigurationAsync("Glooko");

        effective.Should().NotBeNull();

        // The account identifier is present — this is the data the anonymous leak exposed, and the
        // legitimate authenticated caller still needs it to render the configuration form.
        effective!.Should().ContainKey("email");
        effective["email"].Should().Be(accountEmail);

        // Secrets are never part of the effective values.
        effective.Should().NotContainKey("password");
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The configuration form is rendered from this schema, so a toggle in it is a toggle a user can
/// switch — for a data type the connector has no way of producing, that is a control that does
/// nothing.
/// </summary>
public class ConnectorSchemaSyncToggleTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;

    public ConnectorSchemaSyncToggleTests()
    {
        // Force-load the Dexcom connector assembly so the service's reflection-based type lookup
        // (AppDomain scan for [ConnectorRegistration]) can resolve "Dexcom".
        _ = typeof(DexcomConnectorConfiguration);

        _db = TestDbContextFactory.CreateSqlite();

        _dbContext = _db.CreateContext(Guid.CreateVersion7());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GlucoseOnlyConnectorSchemaOffersNoTempBasalToggle()
    {
        // Dexcom Share reports glucose and nothing else: SupportedDataTypes is [Glucose].
        var schema = await CreateService().GetSchemaAsync("Dexcom");

        var properties = schema.RootElement.GetProperty("properties");

        properties.TryGetProperty("syncGlucose", out _).Should().BeTrue(
            "the connector supports glucose, so its toggle belongs in the form");
        properties.TryGetProperty("syncTempBasals", out _).Should().BeFalse();
        properties.TryGetProperty("syncBoluses", out _).Should().BeFalse();
    }

    private ConnectorConfigurationService CreateService() =>
        new(
            _dbContext,
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            NullLogger<ConnectorConfigurationService>.Instance);
}

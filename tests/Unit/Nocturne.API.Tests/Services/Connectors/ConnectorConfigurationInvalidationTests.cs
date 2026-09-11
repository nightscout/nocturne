using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Every write to a connector's stored configuration must reach the tenant-keyed caches: the auth
/// token caches so the next sync uses the new credentials, and the pollers' schedules so a connector
/// the tenant just saved, enabled or removed is looked at on the next tick rather than after its
/// recheck interval. Saving secrets and toggling active are writes too, not only the full save.
/// </summary>
public class ConnectorConfigurationInvalidationTests : IDisposable
{
    private const string ConnectorName = "Glooko";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly List<(string Connector, Guid Tenant)> _invalidated = [];
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationInvalidationTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext(_tenantId);
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "tenant",
            DisplayName = "Tenant",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        var encryption = new Mock<ISecretEncryptionService>();
        encryption.Setup(e => e.IsConfigured).Returns(true);
        encryption
            .Setup(e => e.EncryptSecrets(It.IsAny<Dictionary<string, string>>()))
            .Returns<Dictionary<string, string>>(d => d);

        _service = new ConnectorConfigurationService(
            _dbContext,
            encryption.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            [new RecordingInvalidator(_invalidated)],
            NullLogger<ConnectorConfigurationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveConfigurationAsync_InvalidatesForTheTenant()
    {
        await _service.SaveConfigurationAsync(ConnectorName, JsonDocument.Parse("{\"enabled\":true}"));

        _invalidated.Should().ContainSingle().Which.Should().Be((ConnectorName, _tenantId));
    }

    [Fact]
    public async Task SaveSecretsAsync_InvalidatesForTheTenant()
    {
        await _service.SaveSecretsAsync(ConnectorName, new Dictionary<string, string> { ["password"] = "x" });

        _invalidated.Should().ContainSingle().Which.Should().Be((ConnectorName, _tenantId));
    }

    [Fact]
    public async Task SetActiveAsync_InvalidatesForTheTenant()
    {
        await _service.SetActiveAsync(ConnectorName, isActive: true);

        _invalidated.Should().ContainSingle().Which.Should().Be((ConnectorName, _tenantId));
    }

    [Fact]
    public async Task DeleteConfigurationAsync_InvalidatesOnlyWhenARowWasDeleted()
    {
        (await _service.DeleteConfigurationAsync(ConnectorName)).Should().BeFalse();
        _invalidated.Should().BeEmpty();

        await _service.SetActiveAsync(ConnectorName, isActive: true);
        _invalidated.Clear();

        (await _service.DeleteConfigurationAsync(ConnectorName)).Should().BeTrue();
        _invalidated.Should().ContainSingle().Which.Should().Be((ConnectorName, _tenantId));
    }

    private sealed class RecordingInvalidator(List<(string Connector, Guid Tenant)> sink) : IConnectorCacheInvalidator
    {
        public void Invalidate(string connectorName, Guid tenantId) => sink.Add((connectorName, tenantId));
    }
}

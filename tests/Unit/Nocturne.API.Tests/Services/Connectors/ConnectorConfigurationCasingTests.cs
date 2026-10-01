using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// A connector's configuration is written from two places that spell its name differently: a
/// connector's own code passes the PascalCase name off its registration, while the settings UI
/// passes the lowercase id out of the route. The unique index over (connector_name, tenant_id) is
/// case-sensitive, so a spelling that reaches the column unchanged is a second row for the same
/// connector — which nothing dedupes and every lookup then picks between arbitrarily.
/// </summary>
public class ConnectorConfigurationCasingTests : IDisposable
{
    private const string RegistrationName = "CareLink";
    private const string RouteName = "carelink";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationCasingTests()
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
            [],
            NullLogger<ConnectorConfigurationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveSecretsAsync_StoresTheNameTheRouteWouldUse()
    {
        await _service.SaveSecretsAsync(
            RegistrationName, new Dictionary<string, string> { ["refresh_token"] = "token" });

        var names = await _dbContext.ConnectorConfigurations
            .AsNoTracking()
            .Select(c => c.ConnectorName)
            .ToListAsync();

        names.Should().Equal(RouteName);
    }

    [Fact]
    public async Task SaveConfigurationAsync_AfterSaveSecretsAsync_WithOtherCasing_KeepsOneRow()
    {
        await _service.SaveSecretsAsync(
            RegistrationName, new Dictionary<string, string> { ["refresh_token"] = "token" });
        await _service.SaveConfigurationAsync(
            RouteName, JsonDocument.Parse("""{"enabled":true,"username":"someone"}"""));

        var rows = await _dbContext.ConnectorConfigurations.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle();
        rows[0].ConnectorName.Should().Be(RouteName);
        rows[0].SecretsJson.Should().Contain("refresh_token");
        rows[0].ConfigurationJson.Should().Contain("username");
    }

    [Fact]
    public async Task GetConfigurationAsync_FindsARowWrittenUnderTheOtherCasing()
    {
        await _service.SaveConfigurationAsync(
            RouteName, JsonDocument.Parse("""{"enabled":true}"""));

        var config = await _service.GetConfigurationAsync(RegistrationName);

        config.Should().NotBeNull();
        config!.ConnectorName.Should().Be(RouteName);
    }
}

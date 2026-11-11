using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// A configuration save never owns the enabled flag: the settings form seeds itself from the stored
/// document, so a save that carried the flag back would undo a toggle made after the form loaded.
/// </summary>
public class ConnectorConfigurationEnabledFlagTests : IDisposable
{
    private const string ConnectorName = "glooko";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationEnabledFlagTests()
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

        _service = new ConnectorConfigurationService(
            new ConnectorConfigurationRepository(_dbContext.AsFactory()),
            new SubjectRepository(_dbContext.AsFactory()),
            new OAuthGrantRepository(_dbContext.AsFactory()),
            Mock.Of<ISecretEncryptionService>(),
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

    [Theory]
    [InlineData("""{"region":"us"}""")]
    [InlineData("""{"region":"us","enabled":true}""")]
    public async Task SaveConfigurationAsync_AfterADisable_ReportsAndStoresTheConnectorDisabled(string saved)
    {
        await _service.SaveConfigurationAsync(ConnectorName, JsonDocument.Parse("""{"enabled":true}"""));
        await _service.SetActiveAsync(ConnectorName, isActive: false);

        var response = await _service.SaveConfigurationAsync(ConnectorName, JsonDocument.Parse(saved));

        response.IsActive.Should().BeFalse();
        (await _service.GetConfigurationAsync(ConnectorName))!.IsActive.Should().BeFalse();
        (await StatusAsync()).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ARowWithOnlyAnEnabledKeyInAnotherCase_ReadsAsDisabled()
    {
        _dbContext.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            ConnectorName = ConnectorName,
            ConfigurationJson = """{"Enabled":false}""",
            SecretsJson = "{}",
        });
        await _dbContext.SaveChangesAsync();

        (await _service.GetConfigurationAsync(ConnectorName))!.IsActive.Should().BeFalse();
        (await StatusAsync()).IsEnabled.Should().BeFalse();
    }

    private async Task<ConnectorStatusInfo> StatusAsync() =>
        (await _service.GetAllConnectorStatusAsync()).Single(s => s.ConnectorName == ConnectorName);
}

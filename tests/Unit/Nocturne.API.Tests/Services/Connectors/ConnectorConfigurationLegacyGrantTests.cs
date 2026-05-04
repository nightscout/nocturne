using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

public class ConnectorConfigurationLegacyGrantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Mock<IAuditContext> _auditContext;
    private readonly Mock<ISecretEncryptionService> _encryptionService;
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationLegacyGrantTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(dbOptions) { TenantId = _tenantId };
        _dbContext.Database.EnsureCreated();

        // Seed required entities for FK constraints
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _subjectId,
            Name = "Test User",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        _encryptionService = new Mock<ISecretEncryptionService>();
        _encryptionService.Setup(e => e.IsConfigured).Returns(true);
        _encryptionService
            .Setup(e => e.EncryptSecrets(It.IsAny<Dictionary<string, string>>()))
            .Returns<Dictionary<string, string>>(d => d);

        _auditContext = new Mock<IAuditContext>();
        _auditContext.Setup(a => a.SubjectId).Returns(_subjectId);

        var broadcastService = Mock.Of<ISignalRBroadcastService>();
        var configuration = new ConfigurationBuilder().Build();
        var environment = Mock.Of<IHostEnvironment>();
        var logger = NullLogger<ConnectorConfigurationService>.Instance;

        _service = new ConnectorConfigurationService(
            _dbContext,
            _encryptionService.Object,
            broadcastService,
            _auditContext.Object,
            configuration,
            environment,
            logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutWithApiSecret_CreatesLegacyGrant()
    {
        var apiSecret = "my-nightscout-secret";
        var secrets = new Dictionary<string, string> { ["ApiSecret"] = apiSecret };

        await _service.SaveSecretsAsync("Nightscout", secrets);

        var grant = await _dbContext.OAuthGrants
            .SingleOrDefaultAsync(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct);

        grant.Should().NotBeNull();
        grant!.LegacySecretHash.Should().Be(HashUtils.Sha1Hex(apiSecret));
        grant.Label.Should().Be("Nightscout (migrated)");
        grant.TokenHash.Should().BeNull();
        grant.SubjectId.Should().Be(_subjectId);
        grant.Scopes.Should().Contain("entries.readwrite");
        grant.Scopes.Should().Contain("treatments.readwrite");
        grant.Scopes.Should().Contain("devicestatus.readwrite");
        grant.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutSameSecretTwice_DoesNotDuplicate()
    {
        var apiSecret = "my-nightscout-secret";
        var secrets = new Dictionary<string, string> { ["ApiSecret"] = apiSecret };

        await _service.SaveSecretsAsync("Nightscout", secrets);
        await _service.SaveSecretsAsync("Nightscout", secrets);

        var grants = await _dbContext.OAuthGrants
            .Where(g => g.TenantId == _tenantId
                     && g.GrantType == OAuthGrantTypes.Direct
                     && g.LegacySecretHash != null)
            .ToListAsync();

        grants.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveSecrets_NonNightscoutConnector_DoesNotCreateGrant()
    {
        var secrets = new Dictionary<string, string> { ["ApiSecret"] = "some-secret" };

        await _service.SaveSecretsAsync("Dexcom", secrets);

        var grants = await _dbContext.OAuthGrants
            .Where(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct)
            .ToListAsync();

        grants.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutWithoutApiSecret_DoesNotCreateGrant()
    {
        var secrets = new Dictionary<string, string> { ["Url"] = "https://my.nightscout.example" };

        await _service.SaveSecretsAsync("Nightscout", secrets);

        var grants = await _dbContext.OAuthGrants
            .Where(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct)
            .ToListAsync();

        grants.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutCaseInsensitive_CreatesLegacyGrant()
    {
        var secrets = new Dictionary<string, string> { ["apiSecret"] = "test-secret" };

        await _service.SaveSecretsAsync("nightscout", secrets);

        var grant = await _dbContext.OAuthGrants
            .SingleOrDefaultAsync(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct);

        grant.Should().NotBeNull();
        grant!.LegacySecretHash.Should().Be(HashUtils.Sha1Hex("test-secret"));
    }
}

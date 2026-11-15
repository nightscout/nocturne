using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// Nightscout's sync interval is a choice of four values, where every other connector takes a
/// number of minutes in a range.
/// </summary>
public class ConnectorConfigurationAllowedNumbersTests : IDisposable
{
    private const string Nightscout = "Nightscout";
    private const string Dexcom = "Dexcom";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly ConnectorConfigurationService _service;
    private readonly ConfigurationController _controller;

    public ConnectorConfigurationAllowedNumbersTests()
    {
        _ = typeof(NightscoutConnectorConfiguration);
        _ = typeof(DexcomConnectorConfiguration);

        _db = TestDbContextFactory.CreateSqlite();
        var tenantId = Guid.CreateVersion7();
        _dbContext = _db.CreateContext(tenantId);
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "allowed-numbers",
            DisplayName = "Allowed numbers",
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
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            NullLogger<ConnectorConfigurationService>.Instance);

        _controller = new ConfigurationController(_service, NullLogger<ConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheNightscoutSchemaOffersTheSyncIntervalAsFourChoices()
    {
        var interval = await SyncIntervalSchemaAsync(Nightscout);

        interval.GetProperty("enum").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(1, 5, 60, 240);
        interval.GetProperty("default").GetInt32().Should().Be(5);
        interval.TryGetProperty("minimum", out _).Should().BeFalse();
        interval.TryGetProperty("maximum", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherConnectorKeepsTheSyncIntervalAsARange()
    {
        var interval = await SyncIntervalSchemaAsync(Dexcom);

        interval.TryGetProperty("enum", out _).Should().BeFalse();
        interval.GetProperty("minimum").GetInt32().Should().Be(1);
        interval.GetProperty("maximum").GetInt32().Should().Be(60);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(240)]
    public async Task Save_AcceptsEachOfferedSyncInterval(int minutes)
    {
        var result = await SaveAsync(minutes);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(241)]
    public async Task Save_RefusesASyncIntervalThatIsNotOffered(int minutes)
    {
        var result = await SaveAsync(minutes);

        var body = JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value);
        body.Should().Contain("syncIntervalMinutes").And.Contain("1, 5, 60, 240");
    }

    [Fact]
    public async Task Save_KeepsAStoredSyncIntervalThatIsNoLongerOffered()
    {
        using (var stored = JsonDocument.Parse("""{"url":"https://mysite.example","syncIntervalMinutes":15}"""))
            await _service.SaveConfigurationAsync(Nightscout, stored);

        var unchanged = await _controller.SaveConfiguration(
            Nightscout,
            JsonDocument.Parse("""{"url":"https://other.example","syncIntervalMinutes":15}"""),
            CancellationToken.None);
        var changed = await SaveAsync(20);

        unchanged.Result.Should().BeOfType<OkObjectResult>();
        changed.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Save_OfASyncIntervalWrittenAsTextDoesNotThrow()
    {
        var save = () => _controller.SaveConfiguration(
            Nightscout,
            JsonDocument.Parse("""{"url":"https://mysite.example","syncIntervalMinutes":"60"}"""),
            CancellationToken.None);

        await save.Should().NotThrowAsync();
    }

    private async Task<JsonElement> SyncIntervalSchemaAsync(string connectorName)
    {
        var schema = await _service.GetSchemaAsync(connectorName);
        return schema.RootElement.GetProperty("properties").GetProperty("syncIntervalMinutes");
    }

    private Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(int minutes) =>
        _controller.SaveConfiguration(
            Nightscout,
            JsonDocument.Parse($$"""{"url":"https://mysite.example","syncIntervalMinutes":{{minutes}}}"""),
            CancellationToken.None);
}

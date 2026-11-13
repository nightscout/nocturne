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
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

public class ConnectorConfigurationUnknownKeyTests : IDisposable
{
    private const string ConnectorName = "Dexcom";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly ConnectorConfigurationService _service;
    private readonly ConfigurationController _controller;

    public ConnectorConfigurationUnknownKeyTests()
    {
        _ = typeof(DexcomConnectorConfiguration);

        _db = TestDbContextFactory.CreateSqlite();
        var tenantId = Guid.CreateVersion7();
        _dbContext = _db.CreateContext(tenantId);
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "unknown-keys",
            DisplayName = "Unknown keys",
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
    public async Task Save_RefusesAnUnknownKeyByNameAndStoresNothing()
    {
        var result = await SaveAsync("""{"username":"u","isActive":false}""");

        var refused = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(refused.Value).Should().Contain("isActive");
        (await _service.GetConfigurationAsync(ConnectorName)).Should().BeNull();
    }

    [Fact]
    public async Task Save_NamesEveryUnknownKey()
    {
        var result = await SaveAsync("""{"username":"u","isActive":false,"serverr":"US"}""");

        var body = JsonSerializer.Serialize(result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value);
        body.Should().Contain("isActive").And.Contain("serverr");
    }

    [Fact]
    public async Task Save_AcceptsEveryKeyTheTypeBindsEvenWhenTheSchemaHidesIt()
    {
        // Dexcom reports glucose only, so the schema omits syncTempBasals; the binder still binds it.
        var result = await SaveAsync(
            """{"username":"u","server":"US","syncGlucose":true,"syncTempBasals":false,"enabled":true}""");

        result.Result.Should().BeOfType<OkObjectResult>();
        (await _service.GetConfigurationAsync(ConnectorName)).Should().NotBeNull();
    }

    [Fact]
    public async Task Save_AcceptsTheEnabledFlagInAnyCasing()
    {
        var result = await SaveAsync("""{"username":"u","Enabled":false}""");

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_DropsAStoredKeyTheTypeDoesNotBind()
    {
        await StoreAsync("""{"username":"u","isActive":false,"syncTempBasals":true,"enabled":false}""");

        var stored = await GetConfigurationAsync();

        stored.RootElement.TryGetProperty("isActive", out _).Should().BeFalse();
        stored.RootElement.GetProperty("username").GetString().Should().Be("u");
        stored.RootElement.GetProperty("syncTempBasals").GetBoolean().Should().BeTrue();
        stored.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Get_ThenPut_OfADocumentHoldingAStaleKeySucceedsAndWritesItClean()
    {
        await StoreAsync("""{"username":"u","isActive":false,"removedSetting":1}""");

        var sent = await GetConfigurationAsync();
        var result = await _controller.SaveConfiguration(ConnectorName, sent, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var rewritten = (await _service.GetConfigurationAsync(ConnectorName))!.Configuration.RootElement.GetRawText();
        rewritten.Should().NotContain("isActive").And.NotContain("removedSetting");
    }

    private Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(string body) =>
        _controller.SaveConfiguration(ConnectorName, JsonDocument.Parse(body), CancellationToken.None);

    private async Task StoreAsync(string body)
    {
        using var document = JsonDocument.Parse(body);
        await _service.SaveConfigurationAsync(ConnectorName, document);
    }

    private async Task<JsonDocument> GetConfigurationAsync()
    {
        var result = await _controller.GetConfiguration(ConnectorName, CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ConnectorConfigurationResponse>().Subject.Configuration;
    }
}

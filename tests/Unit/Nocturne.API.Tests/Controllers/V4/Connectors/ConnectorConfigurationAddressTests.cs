using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// The path or query of a connector's address can be the credential, so a read gives only its
/// shown form, and a save keeps the stored address unless it names a new one
/// (<see cref="ConnectorAddresses"/>).
/// </summary>
public class ConnectorConfigurationAddressTests : IDisposable
{
    private const string Nightscout = "Nightscout";
    private const string PathSecret = "AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string StoredUrl = $"https://mine.example/{PathSecret}/?token=synthetic-token";
    private const string ShortPathSecret = "s3cr3t";
    private const string StoredRealtimeUrl = $"https://live.example:8443/{ShortPathSecret}";

    private static readonly string Stored =
        JsonSerializer.Serialize(new { url = StoredUrl, realtimeUrl = StoredRealtimeUrl, maxCount = 1000 });

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationAddressTests()
    {
        _ = typeof(NightscoutConnectorConfiguration);

        _db = TestDbContextFactory.CreateSqlite();
        var tenantId = Guid.CreateVersion7();
        _dbContext = _db.CreateContext(tenantId);
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "addresses",
            DisplayName = "Addresses",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        var encryption = new Mock<ISecretEncryptionService>();
        encryption.SetupGet(e => e.IsConfigured).Returns(true);
        encryption.Setup(e => e.EncryptSecrets(It.IsAny<Dictionary<string, string>>()))
            .Returns((Dictionary<string, string> secrets) => secrets);
        encryption.Setup(e => e.DecryptSecrets(It.IsAny<Dictionary<string, string>>()))
            .Returns((Dictionary<string, string> secrets) => secrets);

        _service = new ConnectorConfigurationService(
            new ConnectorConfigurationRepository(_dbContext.AsFactory()),
            new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
            new OAuthGrantRepository(_dbContext.AsFactory()),
            encryption.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            [],
            TimeProvider.System,
            NullLogger<ConnectorConfigurationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_schema_marks_each_address_and_nothing_else()
    {
        var schema = await _service.GetSchemaAsync(Nightscout);

        schema.RootElement.GetProperty("properties").EnumerateObject()
            .Where(property => property.Value.TryGetProperty(ConnectorAddresses.SchemaMarker, out var marker)
                && marker.ValueKind == JsonValueKind.True)
            .Select(property => property.Name)
            .Should().BeEquivalentTo("url", "realtimeUrl");
    }

    [Fact]
    public async Task A_read_gives_each_address_as_its_host_with_no_path()
    {
        await SeedAsync();

        var read = await Controller(AuthType.SessionCookie, RoleSeeds.Owner).GetConfiguration(Nightscout, default);

        var configuration = Configuration(read);
        configuration.GetProperty("url").GetString().Should().Be("mine.example/…");
        configuration.GetProperty("realtimeUrl").GetString().Should().Be("live.example:8443/…");
        configuration.GetRawText().Should().NotContain(PathSecret).And.NotContain(ShortPathSecret).And.NotContain("synthetic-token");
    }

    [Fact]
    public async Task A_save_answers_with_each_address_as_its_host_with_no_path()
    {
        var saved = await SaveAsync(AuthType.SessionCookie, RoleSeeds.Owner, Stored);

        var configuration = Configuration(saved);
        configuration.GetProperty("url").GetString().Should().Be("mine.example/…");
        configuration.GetRawText().Should().NotContain(PathSecret).And.NotContain(ShortPathSecret).And.NotContain("synthetic-token");
        (await StoredAsync()).GetProperty("url").GetString().Should().Be(StoredUrl);
    }

    [Theory]
    [InlineData("""{"maxCount":500}""")]
    [InlineData("""{"url":"","maxCount":500}""")]
    [InlineData("""{"url":"  ","maxCount":500}""")]
    [InlineData("""{"url":null,"maxCount":500}""")]
    public async Task A_save_that_names_no_new_url_keeps_both_stored_addresses(string proposed)
    {
        await SeedAsync();

        var saved = await SaveAsync(AuthType.SessionCookie, RoleSeeds.Owner, proposed);

        saved.Result.Should().BeOfType<OkObjectResult>();
        var stored = await StoredAsync();
        stored.GetProperty("url").GetString().Should().Be(StoredUrl);
        stored.GetProperty("realtimeUrl").GetString().Should().Be(StoredRealtimeUrl);
        stored.GetProperty("maxCount").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task A_save_that_names_a_new_address_stores_it()
    {
        await SeedAsync();

        await SaveAsync(AuthType.SessionCookie, RoleSeeds.Owner, """{"url":"https://other.example/ns"}""");

        var stored = await StoredAsync();
        stored.GetProperty("url").GetString().Should().Be("https://other.example/ns");
        stored.GetProperty("realtimeUrl").GetString().Should().Be(StoredRealtimeUrl);
    }

    [Fact]
    public async Task A_save_that_sends_a_blank_realtime_url_clears_it()
    {
        await SeedAsync();

        await SaveAsync(AuthType.SessionCookie, RoleSeeds.Owner, """{"realtimeUrl":""}""");

        var stored = await StoredAsync();
        stored.GetProperty("realtimeUrl").GetString().Should().BeEmpty();
        stored.GetProperty("url").GetString().Should().Be(StoredUrl);
    }

    [Fact]
    public async Task A_sent_address_is_stored_as_sent_even_when_it_reads_as_the_shown_form()
    {
        await SeedAsync();

        await SaveAsync(AuthType.SessionCookie, RoleSeeds.Owner, """{"url":"mine.example/…"}""");

        (await StoredAsync()).GetProperty("url").GetString().Should().Be("mine.example/…");
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken, RoleSeeds.Owner)]
    [InlineData(AuthType.SessionCookie, RoleSeeds.Caretaker)]
    public async Task A_guarded_caller_who_leaves_the_addresses_out_is_not_refused(AuthType authType, string role)
    {
        await SeedAsync();
        await _service.SaveRuntimeSecretsAsync(Nightscout, new Dictionary<string, string> { ["apiSecret"] = "s" });

        var saved = await SaveAsync(authType, role, """{"maxCount":500}""");

        saved.Result.Should().BeOfType<OkObjectResult>();
        var stored = await StoredAsync();
        stored.GetProperty("url").GetString().Should().Be(StoredUrl);
        stored.GetProperty("realtimeUrl").GetString().Should().Be(StoredRealtimeUrl);
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken, RoleSeeds.Owner)]
    [InlineData(AuthType.SessionCookie, RoleSeeds.Caretaker)]
    public async Task A_guarded_caller_who_sends_a_blank_realtime_url_is_refused(AuthType authType, string role)
    {
        await SeedAsync();
        await _service.SaveRuntimeSecretsAsync(Nightscout, new Dictionary<string, string> { ["apiSecret"] = "s" });

        var saved = await SaveAsync(authType, role, """{"realtimeUrl":""}""");

        var refusal = saved.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refusal.Value.Should().BeOfType<ProblemDetails>().Subject
            .Extensions[ProblemCodeExtensions.CodeExtension].Should().Be(ConfigurationController.DelegatedChangeRefusedCode);
        (await StoredAsync()).GetProperty("realtimeUrl").GetString().Should().Be(StoredRealtimeUrl);
    }

    private Task SeedAsync() => _service.SaveConfigurationAsync(Nightscout, JsonDocument.Parse(Stored));

    private async Task<JsonElement> StoredAsync() =>
        (await _service.GetConfigurationAsync(Nightscout))!.Configuration.RootElement;

    private static JsonElement Configuration(ActionResult<ConnectorConfigurationResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConnectorConfigurationResponse>().Subject
            .Configuration.RootElement;

    private Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(AuthType authType, string role, string configuration) =>
        Controller(authType, role).SaveConfiguration(Nightscout, JsonDocument.Parse(configuration), default);

    private ConfigurationController Controller(AuthType authType, string role)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = Guid.CreateVersion7(),
        };
        httpContext.Items["GrantedScopes"] = MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[role]), AuthType.SessionCookie, new HashSet<string>());

        return new ConfigurationController(_service, NullLogger<ConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };
    }
}

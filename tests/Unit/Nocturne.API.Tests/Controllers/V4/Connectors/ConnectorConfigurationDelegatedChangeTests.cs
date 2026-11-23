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
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.NocturneRemote.Configurations;
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
/// A delegated credential holding <c>connectors.readwrite</c>, and a member who may set up data
/// sources but not change the site's settings, may set a connector up and change its options, but
/// may not point a connector that holds a secret somewhere new, nor turn on a property that sends
/// the tenant's records out (<see cref="DelegatedConfigurationGuard"/>). A settings holder's own
/// sign-in may do both.
/// </summary>
public class ConnectorConfigurationDelegatedChangeTests : IDisposable
{
    private const string Nightscout = "Nightscout";
    private const string NocturneRemote = "NocturneRemote";
    private const string MyLife = "MyLife";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly ConnectorConfigurationService _service;
    private readonly Mock<IConnectorCacheInvalidator> _invalidator = new();

    public ConnectorConfigurationDelegatedChangeTests()
    {
        _ = typeof(NightscoutConnectorConfiguration);
        _ = typeof(NocturneRemoteConnectorConfiguration);
        _ = typeof(LibreLinkUpConnectorConfiguration);
        _ = typeof(MyLifeConnectorConfiguration);

        _db = TestDbContextFactory.CreateSqlite();
        var tenantId = Guid.CreateVersion7();
        _dbContext = _db.CreateContext(tenantId);
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "delegated-changes",
            DisplayName = "Delegated changes",
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
            [_invalidator.Object],
            TimeProvider.System,
            NullLogger<ConnectorConfigurationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- A: the destination of a connector that holds a secret ----

    [Fact]
    public async Task Delegated_ChangingTheUrlWhileASecretIsStored_IsRefusedAndChangesNothing()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(AuthType.OAuthAccessToken, Nightscout, """{"url":"https://attacker.example"}""");

        ShouldBeRefused(result, "url");
        (await StoredAsync(Nightscout)).Should().Contain("https://mine.example").And.NotContain("attacker");
        _invalidator.Invocations.Should().BeEmpty("a refused save must not nudge the connector into a sync");
    }

    [Fact]
    public async Task Delegated_ChangingTheRealtimeUrlWhileASecretIsStored_IsRefused()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(
            AuthType.DirectGrant, Nightscout, """{"url":"https://mine.example","realtimeUrl":"https://attacker.example"}""");

        ShouldBeRefused(result, "realtimeUrl");
    }

    [Fact]
    public async Task Delegated_ChangingAnOptionWhileASecretIsStored_IsAccepted()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(AuthType.OAuthAccessToken, Nightscout, """{"url":"https://mine.example","maxCount":500}""");

        result.Result.Should().BeOfType<OkObjectResult>();
        (await StoredAsync(Nightscout)).Should().Contain("500");
    }

    [Fact]
    public async Task Interactive_ChangingTheUrlWhileASecretIsStored_IsAccepted()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(AuthType.SessionCookie, Nightscout, """{"url":"https://other.example"}""");

        result.Result.Should().BeOfType<OkObjectResult>();
        (await StoredAsync(Nightscout)).Should().Contain("https://other.example");
    }

    [Fact]
    public async Task Delegated_FirstTimeSetup_SavesTheDestinationThenTheSecretThenVerifies()
    {
        var controller = Controller(AuthType.OAuthAccessToken);

        (await controller.SaveConfiguration(
                NocturneRemote, JsonDocument.Parse("""{"url":"https://remote.example"}"""), default))
            .Result.Should().BeOfType<OkObjectResult>();

        (await controller.SaveSecrets(
                NocturneRemote, new Dictionary<string, string> { ["accessToken"] = "t" }, default))
            .Should().BeOfType<OkObjectResult>();

        var verifier = new RecordingVerifier(_service);
        var verified = await controller.VerifyCredentials(
            NocturneRemote,
            new VerifyConnectorCredentialsRequest { StoredSecrets = ["accessToken"] },
            [verifier],
            default);

        verified.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ConnectorCredentialVerificationResult>()
            .Which.Should().BeEquivalentTo(ConnectorCredentialVerificationResult.Verified());
        verifier.Url.Should().Be("https://remote.example");
        verifier.AccessToken.Should().Be("t");
    }

    // ---- B: a property that sends the tenant's records out ----

    [Fact]
    public async Task Delegated_EnablingWriteBack_IsRefused()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(
            AuthType.OAuthAccessToken, Nightscout, """{"url":"https://mine.example","writeBackEnabled":true}""");

        ShouldBeRefused(result, "writeBackEnabled");
        (await StoredAsync(Nightscout)).Should().NotContain("writeBackEnabled");
    }

    [Fact]
    public async Task Delegated_EnablingWriteBackOnANewConnector_IsRefusedAndStoresNothing()
    {
        var result = await SaveAsync(
            AuthType.OAuthAccessToken, Nightscout, """{"url":"https://attacker.example","writeBackEnabled":true}""");

        ShouldBeRefused(result, "writeBackEnabled");
        (await _service.GetConfigurationAsync(Nightscout)).Should().BeNull();
    }

    [Fact]
    public async Task Delegated_EmptyingTheSecretsThenChangingTheUrlWithWriteBackOn_IsRefused()
    {
        await SeedAsync(
            Nightscout, """{"url":"https://mine.example","writeBackEnabled":true}""", new() { ["apiSecret"] = "s" });
        var controller = Controller(AuthType.OAuthAccessToken);

        (await controller.SaveSecrets(Nightscout, [], default)).Should().BeOfType<OkObjectResult>();
        var result = await controller.SaveConfiguration(
            Nightscout,
            JsonDocument.Parse("""{"url":"https://attacker.example","writeBackEnabled":true}"""),
            default);

        ShouldBeRefused(result, "url");
        (await StoredAsync(Nightscout)).Should().Contain("https://mine.example").And.NotContain("attacker");
    }

    [Fact]
    public async Task Delegated_TurningWriteBackOffWhileChangingTheUrl_IsRefused()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example","writeBackEnabled":true}""", []);

        var result = await SaveAsync(
            AuthType.OAuthAccessToken, Nightscout, """{"url":"https://attacker.example","writeBackEnabled":false}""");

        ShouldBeRefused(result, "url");
    }

    [Fact]
    public async Task Delegated_FirstNightscoutSaveWithWriteBackOff_IsAccepted()
    {
        var result = await SaveAsync(
            AuthType.OAuthAccessToken, Nightscout, """{"url":"https://mine.example","writeBackEnabled":false}""");

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Interactive_MayEmptyTheSecretsThenChangeTheUrlWithWriteBackOn()
    {
        await SeedAsync(
            Nightscout, """{"url":"https://mine.example","writeBackEnabled":true}""", new() { ["apiSecret"] = "s" });
        var controller = Controller(AuthType.SessionCookie);

        (await controller.SaveSecrets(Nightscout, [], default)).Should().BeOfType<OkObjectResult>();
        (await controller.SaveConfiguration(
                Nightscout,
                JsonDocument.Parse("""{"url":"https://other.example","writeBackEnabled":true}"""),
                default))
            .Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Interactive_MaySetUpANewConnectorWithWriteBackOn()
    {
        var result = await SaveAsync(
            AuthType.SessionCookie, Nightscout, """{"url":"https://mine.example","writeBackEnabled":true}""");

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    // ---- a member who sets up data sources but cannot change the site's settings ----

    /// <summary>
    /// Every destination <see cref="DelegatedConfigurationGuard"/> guards: the stored configuration,
    /// the same document with the destination moved, and the connector's secret.
    /// </summary>
    public static TheoryData<string, string, string, string, string> Destinations => new()
    {
        { Nightscout, "url", """{"url":"https://mine.example"}""", """{"url":"https://attacker.example"}""", "apiSecret" },
        { Nightscout, "realtimeUrl", """{"url":"https://mine.example"}""", """{"url":"https://mine.example","realtimeUrl":"https://attacker.example"}""", "apiSecret" },
        { NocturneRemote, "url", """{"url":"https://mine.example"}""", """{"url":"https://attacker.example"}""", "accessToken" },
        { MyLife, "serviceUrl", """{"username":"u","serviceUrl":"https://mine.example"}""", """{"username":"u","serviceUrl":"https://attacker.example"}""", "password" },
    };

    [Theory]
    [MemberData(nameof(Destinations))]
    public async Task Caretaker_MovingADestinationWhileASecretIsStored_IsRefusedAndChangesNothing(
        string connector, string key, string stored, string proposed, string secret)
    {
        await SeedAsync(connector, stored, new() { [secret] = "s" });

        var result = await Controller(AuthType.SessionCookie, RoleSeeds.Caretaker)
            .SaveConfiguration(connector, JsonDocument.Parse(proposed), default);

        ShouldBeRefused(result, key);
        (await StoredAsync(connector)).Should().NotContain("attacker");
    }

    [Theory]
    [MemberData(nameof(Destinations))]
    public async Task Caretaker_MovingADestinationWithNoSecretStored_IsAccepted(
        string connector, string key, string stored, string proposed, string secret)
    {
        _ = (key, secret);
        await SeedAsync(connector, stored, []);

        var result = await Controller(AuthType.SessionCookie, RoleSeeds.Caretaker)
            .SaveConfiguration(connector, JsonDocument.Parse(proposed), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await StoredAsync(connector)).Should().Contain("https://attacker.example");
    }

    [Theory]
    [MemberData(nameof(Destinations))]
    public async Task SettingsHolder_MovingADestinationWhileASecretIsStored_IsAccepted(
        string connector, string key, string stored, string proposed, string secret)
    {
        _ = key;
        await SeedAsync(connector, stored, new() { [secret] = "s" });

        var result = await Controller(AuthType.SessionCookie, RoleSeeds.Admin)
            .SaveConfiguration(connector, JsonDocument.Parse(proposed), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        (await StoredAsync(connector)).Should().Contain("https://attacker.example");
    }

    [Theory]
    [MemberData(nameof(Destinations))]
    public async Task Caretaker_IsToldTheDestinationIsLockedAndASettingsHolderIsNot(
        string connector, string key, string stored, string proposed, string secret)
    {
        _ = proposed;
        await SeedAsync(connector, stored, new() { [secret] = "s" });

        (await LockedKeysAsync(RoleSeeds.Caretaker, connector)).Should().Contain(key);
        (await LockedKeysAsync(RoleSeeds.Admin, connector)).Should().BeEmpty();
    }

    [Fact]
    public async Task Caretaker_EnablingWriteBack_IsRefused()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", []);

        var result = await Controller(AuthType.SessionCookie, RoleSeeds.Caretaker).SaveConfiguration(
            Nightscout, JsonDocument.Parse("""{"url":"https://mine.example","writeBackEnabled":true}"""), default);

        ShouldBeRefused(result, "writeBackEnabled");
    }

    [Fact]
    public async Task Caretaker_ChangingAnOptionWhileASecretIsStored_IsAccepted()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await Controller(AuthType.SessionCookie, RoleSeeds.Caretaker).SaveConfiguration(
            Nightscout, JsonDocument.Parse("""{"url":"https://mine.example","maxCount":500}"""), default);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private async Task<IReadOnlyList<string>> LockedKeysAsync(string role, string connector) =>
        (await Controller(AuthType.SessionCookie, role).GetConfiguration(connector, default))
        .Result.Should().BeOfType<OkObjectResult>().Subject
        .Value.Should().BeOfType<ConnectorConfigurationResponse>().Subject.LockedKeys;

    // ---- connector names ----

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.OAuthAccessToken)]
    public async Task ANameThatIsNotAConnector_Is404AndWritesNothing(AuthType authType)
    {
        var result = await SaveAsync(authType, "notaconnector", """{"url":"https://attacker.example"}""");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        (await _service.GetConfigurationAsync("notaconnector")).Should().BeNull();
    }

    [Fact]
    public async Task AKelvinSignName_IsTheRealConnectorAndIsValidated()
    {
        const string kelvin = "librelin\u212Aup";

        var refused = await SaveAsync(AuthType.OAuthAccessToken, kelvin, """{"username":"u","bogus":1}""");
        JsonSerializer.Serialize(refused.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)
            .Should().Contain("bogus");

        (await SaveAsync(AuthType.OAuthAccessToken, kelvin, """{"username":"u"}"""))
            .Result.Should().BeOfType<OkObjectResult>();
        (await _service.GetConfigurationAsync("librelinkup")).Should().NotBeNull();
    }

    [Fact]
    public async Task Interactive_EnablingWriteBack_IsAccepted()
    {
        await SeedAsync(Nightscout, """{"url":"https://mine.example"}""", new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(
            AuthType.SessionCookie, Nightscout, """{"url":"https://mine.example","writeBackEnabled":true}""");

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("""{"url":"https://mine.example"}""", """{"url":"https://mine.example","writeBackEnabled":false}""")]
    [InlineData("""{"url":"https://mine.example","writeBackEnabled":true}""", """{"url":"https://mine.example","writeBackEnabled":true,"maxCount":500}""")]
    public async Task Delegated_LeavingWriteBackOffOrUnchanged_IsAccepted(string stored, string proposed)
    {
        await SeedAsync(Nightscout, stored, new() { ["apiSecret"] = "s" });

        var result = await SaveAsync(AuthType.OAuthAccessToken, Nightscout, proposed);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    // ---- helpers ----

    private async Task SeedAsync(string connector, string configuration, Dictionary<string, string> secrets)
    {
        await _service.SaveConfigurationAsync(connector, JsonDocument.Parse(configuration));
        await _service.SaveRuntimeSecretsAsync(connector, secrets);
        _invalidator.Invocations.Clear();
    }

    private async Task<string> StoredAsync(string connector) =>
        (await _service.GetConfigurationAsync(connector))!.Configuration.RootElement.GetRawText();

    private Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(
        AuthType authType, string connector, string configuration) =>
        Controller(authType).SaveConfiguration(connector, JsonDocument.Parse(configuration), default);

    private static void ShouldBeRefused(ActionResult<ConnectorConfigurationResponse> result, string key)
    {
        var refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = refusal.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions[ProblemCodeExtensions.CodeExtension]
            .Should().Be(ConfigurationController.DelegatedChangeRefusedCode);
        problem.Detail.Should().Contain(key);
    }

    private ConfigurationController Controller(AuthType authType) => Controller(authType, RoleSeeds.Owner);

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

    private sealed class RecordingVerifier(IConnectorConfigurationService configService)
        : ConnectorCredentialVerifier<NocturneRemoteConnectorConfiguration>(
            new Registration(), configService)
    {
        public string? Url { get; private set; }

        public string? AccessToken { get; private set; }

        public override string ConnectorId => NocturneRemote;

        protected override Task<ConnectorCredentialVerificationResult> VerifyConfiguredAsync(
            NocturneRemoteConnectorConfiguration config, CancellationToken ct)
        {
            Url = config.Url;
            AccessToken = config.AccessToken;
            return Task.FromResult(ConnectorCredentialVerificationResult.Verified());
        }

        private sealed class Registration : IConnectorRegistration<NocturneRemoteConnectorConfiguration>
        {
            public NocturneRemoteConnectorConfiguration Defaults { get; } = new();

            public string ConnectorName => NocturneRemote;
        }
    }
}

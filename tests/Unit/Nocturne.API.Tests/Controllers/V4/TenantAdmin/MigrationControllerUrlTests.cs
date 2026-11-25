using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Middleware;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Services.Migration;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.TenantAdmin;

/// <summary>
/// A Nightscout URL is accepted as the connector accepts it, a bare host included, and anything
/// that is not an http or https address is refused before a job starts.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationControllerUrlTests
{
    private readonly Mock<IMigrationJobService> _migrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectors = new();

    public MigrationControllerUrlTests()
    {
        _migrations
            .Setup(m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CredentialMaker?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MigrationJobInfo { Id = Guid.CreateVersion7(), Mode = MigrationMode.Api, CreatedAt = DateTime.UtcNow });
        _connectors
            .Setup(c => c.GetSecretsAsync("nightscout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
    }

    private MigrationController Controller(HttpContext? http = null) =>
        new(_migrations.Object, _connectors.Object, Mock.Of<ITenantAccessor>(), TimeProvider.System, NullLogger<MigrationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http ?? new DefaultHttpContext() },
        };

    private static HttpContext InactiveCaller()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = subjectId, TenantId = tenantId };
        new RequestMembership(subjectId, tenantId, null) { SubjectIsInactive = true }.StoreOn(http);
        return http;
    }

    [Fact]
    public async Task An_inactive_caller_cannot_start_a_migration_and_no_job_starts()
    {
        var controller = Controller(InactiveCaller());

        var started = await controller.StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = "https://ns.example" }, CancellationToken.None);
        var fromConnector = await controller.StartFromConnector("nightscout", CancellationToken.None);

        started.Result.Should().BeOfType<ChallengeResult>();
        fromConnector.Result.Should().BeOfType<ChallengeResult>();
        _migrations.Verify(
            m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CredentialMaker?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void ConnectorUrl(string url) =>
        _connectors
            .Setup(c => c.GetConfigurationAsync("nightscout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "nightscout",
                Configuration = JsonDocument.Parse(JsonSerializer.Serialize(new { url })),
            });

    [Theory]
    [InlineData(30, true)]
    [InlineData(0, false)]
    public async Task StartFromConnector_imports_the_history_the_connector_is_set_to(int historyDays, bool bounded)
    {
        _connectors
            .Setup(c => c.GetConfigurationAsync("nightscout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "nightscout",
                Configuration = JsonDocument.Parse(JsonSerializer.Serialize(new { url = "https://ns.example", historyDays })),
            });
        StartMigrationRequest? started = null;
        _migrations
            .Setup(m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CredentialMaker?>(), It.IsAny<CancellationToken>()))
            .Callback<StartMigrationRequest, TenantContext?, CredentialMaker?, CancellationToken>((request, _, _, _) => started = request)
            .ReturnsAsync(new MigrationJobInfo { Id = Guid.CreateVersion7(), Mode = MigrationMode.Api, CreatedAt = DateTime.UtcNow });

        await Controller().StartFromConnector("nightscout", CancellationToken.None);

        if (bounded)
            started!.StartDate.Should().BeCloseTo(DateTime.UtcNow.AddDays(-historyDays), TimeSpan.FromMinutes(1));
        else
            started!.StartDate.Should().BeNull();
    }

    private static void ShouldBeRefused(IActionResult? result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(NightscoutAddress.InvalidUrlMessage);
    }

    [Theory]
    [InlineData("ftp://ns.example/nightscout")]
    [InlineData("/nightscout")]
    public async Task StartMigration_refuses_an_address_that_is_not_http(string url)
    {
        var response = await Controller().StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = url }, CancellationToken.None);

        ShouldBeRefused(response.Result);
        _migrations.Verify(
            m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CredentialMaker?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("ns.example/nightscout")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token")]
    public async Task StartMigration_accepts_what_the_connector_accepts(string url)
    {
        var response = await Controller().StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = url }, CancellationToken.None);

        response.Result.Should().BeOfType<AcceptedAtActionResult>();
    }

    [Fact]
    public async Task StartFromConnector_refuses_a_saved_address_that_is_not_http()
    {
        ConnectorUrl("ftp://ns.example/nightscout");

        var response = await Controller().StartFromConnector("nightscout", CancellationToken.None);

        ShouldBeRefused(response.Result);
        _migrations.Verify(
            m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CredentialMaker?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartFromConnector_starts_from_a_saved_bare_host()
    {
        ConnectorUrl("ns.example/nightscout");

        var response = await Controller().StartFromConnector("nightscout", CancellationToken.None);

        response.Result.Should().BeOfType<AcceptedAtActionResult>();
        _migrations.Verify(m => m.StartMigrationAsync(
            It.Is<StartMigrationRequest>(r => r.NightscoutUrl == "ns.example/nightscout"),
            It.IsAny<TenantContext?>(),
            It.IsAny<CredentialMaker?>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Each_start_names_the_caller_and_their_access_end_as_the_starter()
    {
        ConnectorUrl("https://ns.example");
        var subjectId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var end = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = subjectId, TenantId = tenantId };
        var access = new TenantMemberAccess(new HashSet<string> { Scope.FullAccess }, false, end);
        new RequestMembership(subjectId, tenantId, new MembershipFacts(Guid.CreateVersion7(), access, access, null)).StoreOn(http);
        var controller = Controller(http);

        await controller.StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = "https://ns.example" }, CancellationToken.None);
        await controller.StartFromConnector("nightscout", CancellationToken.None);

        _migrations.Verify(
            m => m.StartMigrationAsync(
                It.IsAny<StartMigrationRequest>(),
                It.IsAny<TenantContext?>(),
                new CredentialMaker(subjectId, end),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}

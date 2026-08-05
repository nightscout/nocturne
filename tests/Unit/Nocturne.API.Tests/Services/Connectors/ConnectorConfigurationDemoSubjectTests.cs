using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Connector configuration is guarded on the asset, not only on the controllers that reach it.
/// </summary>
/// <remarks>
/// Gating controllers missed a writer: <c>CareLinkConnectController</c> persists the signed-in
/// CareLink username and country here after a Medtronic sign-in, and <c>GET</c> returns
/// configuration in the clear to any member. Every demo visitor is the same member, so a visitor
/// who completed that flow with their real Medtronic account handed their health-account
/// identifier to every later visitor and pulled their CGM data into the shared tenant. Guarding
/// where the row is written covers that path, the configuration endpoint, and anything added
/// later, without a third attribute to remember.
/// </remarks>
public class ConnectorConfigurationDemoSubjectTests : IDisposable
{
    private const string ConnectorName = "CareLink";

    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<IAuditContext> _auditContext = new();
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationDemoSubjectTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(dbOptions) { TenantId = _tenantId };
        _dbContext.Database.EnsureCreated();
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "demo",
            DisplayName = "Nocturne Demo",
            IsActive = true,
            IsDemo = true,
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
            _auditContext.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            NullLogger<ConnectorConfigurationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveConfiguration_IsRefusedForTheSharedDemoSubject()
    {
        SetCaller(isDemoSubject: true);

        var act = async () => await _service.SaveConfigurationAsync(
            ConnectorName, CareLinkAccount("visitor@example.com", "GB"), "carelink-connect");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _dbContext.ConnectorConfigurations.Should().BeEmpty(
            "the visitor's CareLink username must never reach the shared tenant's configuration");
    }

    [Fact]
    public async Task SaveSecrets_IsRefusedForTheSharedDemoSubject()
    {
        SetCaller(isDemoSubject: true);

        var act = async () => await _service.SaveSecretsAsync(
            ConnectorName, new Dictionary<string, string> { ["RefreshToken"] = "their-token" },
            "carelink-connect");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SetActive_IsRefusedForTheSharedDemoSubject()
    {
        // Enabling a connector starts it fetching on the tenant's behalf. Guarded because the
        // stated goal is that every write method is covered without an attribute to remember —
        // ConfigurationController's class-level attribute blocks the route today, but a second
        // caller reaching the service directly would not be.
        SetCaller(isDemoSubject: true);

        var act = async () => await _service.SetActiveAsync(ConnectorName, true, "visitor");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task DeleteConfiguration_IsRefusedForTheSharedDemoSubject()
    {
        SetCaller(isDemoSubject: false);
        await _service.SaveConfigurationAsync(
            ConnectorName, CareLinkAccount("owner@example.com", "AU"), "owner");

        SetCaller(isDemoSubject: true);

        var act = async () => await _service.DeleteConfigurationAsync(ConnectorName);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _dbContext.ConnectorConfigurations.Should().ContainSingle(
            "the visitor must not be able to delete a real member's connector setup");
    }

    [Fact]
    public async Task EveryWriteMethodOnTheServiceIsGuarded()
    {
        // Pins the completeness claim itself. The four write methods on the interface must all
        // refuse the demo subject; if a fifth is added, this fails rather than the claim quietly
        // becoming false.
        SetCaller(isDemoSubject: true);

        var writes = new List<(string Name, Func<Task> Invoke)>
        {
            (nameof(IConnectorConfigurationService.SaveConfigurationAsync),
                () => _service.SaveConfigurationAsync(ConnectorName, CareLinkAccount("a@b.c", "AU"))),
            (nameof(IConnectorConfigurationService.SaveSecretsAsync),
                () => _service.SaveSecretsAsync(ConnectorName, new Dictionary<string, string> { ["T"] = "v" })),
            (nameof(IConnectorConfigurationService.SetActiveAsync),
                () => _service.SetActiveAsync(ConnectorName, true)),
            (nameof(IConnectorConfigurationService.DeleteConfigurationAsync),
                () => _service.DeleteConfigurationAsync(ConnectorName)),
        };

        var writeMethodCount = typeof(IConnectorConfigurationService)
            .GetMethods()
            .Count(m => m.Name.StartsWith("Save", StringComparison.Ordinal)
                        || m.Name.StartsWith("Set", StringComparison.Ordinal)
                        || m.Name.StartsWith("Delete", StringComparison.Ordinal));

        writes.Should().HaveCount(writeMethodCount,
            "a write method was added to IConnectorConfigurationService without being guarded here");

        foreach (var (name, invoke) in writes)
        {
            var act = invoke;
            await act.Should().ThrowAsync<UnauthorizedAccessException>(
                "{0} writes connector configuration", name);
        }
    }

    [Fact]
    public async Task SaveConfiguration_IsAllowedForAnOrdinaryMember()
    {
        SetCaller(isDemoSubject: false);

        await _service.SaveConfigurationAsync(
            ConnectorName, CareLinkAccount("owner@example.com", "AU"), "owner");

        _dbContext.ConnectorConfigurations.Should().ContainSingle(
            "a real member connecting their own account is the point of the flow");
    }

    [Fact]
    public async Task SaveConfiguration_IsAllowedWhenThereIsNoSubject()
    {
        // Instance-key and background callers carry no subject and are not demo visitors.
        _auditContext.Setup(a => a.SubjectId).Returns((Guid?)null);

        await _service.SaveConfigurationAsync(
            ConnectorName, CareLinkAccount("service@example.com", "AU"), "service");

        _dbContext.ConnectorConfigurations.Should().ContainSingle();
    }

    private void SetCaller(bool isDemoSubject)
    {
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = isDemoSubject ? "Demo Visitor" : "Owner",
            IsActive = true,
            IsDemoSubject = isDemoSubject,
        };
        _dbContext.Subjects.Add(subject);
        _dbContext.SaveChanges();
        _auditContext.Setup(a => a.SubjectId).Returns(subject.Id);
    }

    private static JsonDocument CareLinkAccount(string username, string country) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["username"] = username,
            ["country"] = country,
        }));
}

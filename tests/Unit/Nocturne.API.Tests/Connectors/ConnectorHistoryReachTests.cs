using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// A connector declares how far back its source serves once, on its registration. The connector
/// dialogs show that declaration and a range with no lower bound starts at it, so neither may
/// carry a figure of its own.
/// </summary>
public class ConnectorHistoryReachTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static TheoryData<string> RegisteredConnectors() =>
        [.. Registrations().Select(r => r.ConnectorName)];

    public static TheoryData<Type> ConnectorServices() =>
        [.. ConnectorInstallers.Types()
            .Where(t => t is { IsAbstract: false }
                        && ConnectorInstallers.ClosedBaseOf(t, typeof(BaseConnectorService<>)) is not null)];

    [Theory]
    [MemberData(nameof(RegisteredConnectors))]
    public async Task TheCapabilityShownToAnUnconfiguredTenantIsTheDeclarationTheRePullReads(string connectorName)
    {
        var registration = Registrations().Single(r => r.ConnectorName == connectorName);
        using var db = TestDbContextFactory.CreateSqlite();
        using var context = db.CreateContext(TenantId);

        var capabilities = await CreateService(context)
            .GetConnectorCapabilitiesAsync(registration.ConnectorId, readsConfiguration: true);

        capabilities.Should().NotBeNull();
        capabilities!.HistoryReach.Should().Be(registration.HistoryReach);
    }

    /// <summary>
    /// Glooko's SSV2 reads the account from its first record and its windowed path six months, so
    /// the dialogs show the reach of the protocol the tenant's run uses: its stored row bound over the
    /// instance's settings. A caller who may not read the tenant's connector configuration, and a
    /// tenant with no row, are shown the declaration.
    /// </summary>
    [Theory]
    [InlineData("{\"useSsv2Sync\":true}", false, true, HistoryReachUnit.Whole, 0)]
    [InlineData("{\"useSsv2Sync\":false}", false, true, HistoryReachUnit.Months, 6)]
    [InlineData("{}", false, true, HistoryReachUnit.Months, 6)]
    [InlineData(null, false, true, HistoryReachUnit.Months, 6)]
    [InlineData("{\"useSsv2Sync\":true}", false, false, HistoryReachUnit.Months, 6)]
    [InlineData("{}", true, true, HistoryReachUnit.Whole, 0)]
    [InlineData("{\"useSsv2Sync\":false}", true, true, HistoryReachUnit.Months, 6)]
    [InlineData(null, true, true, HistoryReachUnit.Months, 6)]
    public async Task GlookoCapabilitiesShowTheReachOfTheTenantsProtocol(
        string? stored, bool instanceSsv2, bool readsConfiguration, HistoryReachUnit unit, int amount)
    {
        using var db = TestDbContextFactory.CreateSqlite();
        using var context = db.CreateContext(TenantId);
        context.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "tenant", DisplayName = "Tenant", IsActive = true });
        await context.SaveChangesAsync();
        var repository = new ConnectorConfigurationRepository(context.AsFactory());
        if (stored is not null)
            await repository.SaveConfigurationAsync("glooko", stored, null, CancellationToken.None);
        var instance = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connectors:Glooko:UseSsv2Sync"] = instanceSsv2 ? "true" : null,
            })
            .Build();

        var capabilities = await CreateService(context, ConfigurationService(context, repository, instance))
            .GetConnectorCapabilitiesAsync("glooko", readsConfiguration);

        capabilities!.HistoryReach.Should().Be(new ConnectorHistoryReach(unit, amount));
    }

    [Theory]
    [MemberData(nameof(RegisteredConnectors))]
    public void ADeclaredReachNamesAnAmountExactlyWhenItIsBounded(string connectorName)
    {
        var reach = Registrations().Single(r => r.ConnectorName == connectorName).HistoryReach;

        (reach.Unit == HistoryReachUnit.Whole).Should().Be(reach.Amount == 0);
        reach.Amount.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// A connector overriding where its range starts has a figure of its own, which the dialogs
    /// would never show. Only a source serving its whole history needs one, for a concrete start.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConnectorServices))]
    public void OnlyAConnectorServingItsWholeHistoryOverridesWhereARangeStarts(Type service)
    {
        var overrides = service.GetMethod(
            "HistoryStart", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null;
        var config = ConnectorInstallers.ClosedBaseOf(service, typeof(BaseConnectorService<>))!.GetGenericArguments()[0];

        if (overrides)
            ConnectorRegistrationAttribute.DeclaredOn(config).HistoryReach.Should().Be(ConnectorHistoryReach.Whole);
    }

    private static List<ConnectorRegistrationAttribute> Registrations() =>
        [.. ConnectorInstallers.Types()
            .Select(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false))
            .OfType<ConnectorRegistrationAttribute>()
            .DistinctBy(r => r.ConnectorName)];

    private static ConnectorConfigurationService ConfigurationService(
        NocturneDbContext context, ConnectorConfigurationRepository repository, IConfiguration instance) =>
        new(
            repository,
            new SubjectRepository(context.AsFactory(), AccountLinkRoutes.Off),
            new OAuthGrantRepository(context.AsFactory()),
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            instance,
            Mock.Of<IHostEnvironment>(),
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            TimeProvider.System,
            NullLogger<ConnectorConfigurationService>.Instance);

    private static DataSourceService CreateService(
        NocturneDbContext context, IConnectorConfigurationService? configurations = null) => new(
        Mock.Of<ISourceOwnedDataReader>(),
        Mock.Of<ISourceOwnedDataPurge>(),
        Mock.Of<IDemoDataPurge>(),
        Mock.Of<IConnectorConfigurationRepository>(),
        Mock.Of<ISensorGlucoseRepository>(),
        Mock.Of<IMeterGlucoseRepository>(),
        Mock.Of<ICalibrationRepository>(),
        Mock.Of<IAuditContext>(),
        configurations ?? Mock.Of<IConnectorConfigurationService>(),
        Nocturne.API.Tests.Infrastructure.ProfileActivationTestDeps.Writer(context),
        [],
        [],
        TimeProvider.System,
        NullLogger<DataSourceService>.Instance);
}

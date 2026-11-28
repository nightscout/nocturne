using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Demo;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
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
    public void TheCapabilityShownIsTheDeclarationTheRePullReads(string connectorName)
    {
        var registration = Registrations().Single(r => r.ConnectorName == connectorName);
        using var db = TestDbContextFactory.CreateSqlite();
        using var context = db.CreateContext(TenantId);

        var capabilities = CreateService(context).GetConnectorCapabilities(registration.ConnectorId);

        capabilities.Should().NotBeNull();
        capabilities!.HistoryReach.Should().Be(registration.HistoryReach);
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

    private static DataSourceService CreateService(NocturneDbContext context) => new(
        Mock.Of<ISourceOwnedDataReader>(),
        Mock.Of<ISourceOwnedDataPurge>(),
        Mock.Of<IDemoDataPurge>(),
        Mock.Of<IConnectorConfigurationRepository>(),
        Mock.Of<ISensorGlucoseRepository>(),
        Mock.Of<IMeterGlucoseRepository>(),
        Mock.Of<ICalibrationRepository>(),
        Mock.Of<IAuditContext>(),
        Mock.Of<IConnectorConfigurationService>(),
        Nocturne.API.Tests.Infrastructure.ProfileActivationTestDeps.Writer(context),
        [],
        [],
        TimeProvider.System,
        NullLogger<DataSourceService>.Instance);
}

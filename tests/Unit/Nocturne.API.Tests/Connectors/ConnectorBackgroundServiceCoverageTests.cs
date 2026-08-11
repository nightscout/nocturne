using System.Reflection;
using FluentAssertions;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Every connector that polls an external source must have a
/// <see cref="ConnectorBackgroundService{TConfig}"/> subclass in the API assembly.
/// </summary>
/// <remarks>
/// <c>AddConnectors</c> registers hosted services by scanning the API assembly for
/// <see cref="ConnectorBackgroundService{TConfig}"/> subclasses and reading
/// <see cref="ConnectorRegistrationAttribute"/> off <c>TConfig</c> — the config side is what makes a
/// connector appear in the UI and accept credentials, and nothing ties the two together. A connector
/// whose config carries the attribute but has no service class installs, shows as connected, and
/// then never syncs, with no error anywhere.
/// <para>
/// Configs are discovered by reflection over the loaded connector assemblies rather than listed, so
/// a new connector project is covered without editing this file.
/// </para>
/// </remarks>
public class ConnectorBackgroundServiceCoverageTests
{
    /// <summary>
    /// Connectors that never poll: they receive or emit data by another route, so having no
    /// background service is the design, not a gap.
    /// </summary>
    private static readonly HashSet<string> NonPollingByDesign =
        new(StringComparer.Ordinal)
        {
            // Outbound notify target: the alert delivery pipeline pushes to it.
            "HomeAssistant",
        };

    /// <summary>
    /// Polling connectors that are shipped without a background service and therefore never sync.
    /// Each entry is a live gap; removing one is what this guard is for.
    /// </summary>
    private static readonly HashSet<string> PollingWithoutASyncServiceYet =
        new(StringComparer.Ordinal)
        {
            "Tandem",
        };

    public static TheoryData<string> PollingConnectors()
    {
        var data = new TheoryData<string>();
        foreach (var name in DiscoverConnectorNames()
                     .Where(n => !NonPollingByDesign.Contains(n)
                                 && !PollingWithoutASyncServiceYet.Contains(n)))
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void AttributedConnectorConfigurations_AreDiscovered()
    {
        // Guards the guard: if reflection finds nothing, every theory case below silently vanishes
        // and this file would pass while testing nothing at all.
        DiscoverConnectorNames().Should().HaveCountGreaterThan(5,
            "connector configurations are discovered by reflection; finding none would make the " +
            "coverage theory vacuous");
    }

    [Theory]
    [MemberData(nameof(PollingConnectors))]
    public void EveryPollingConnector_HasABackgroundSyncService(string connectorName)
    {
        var configType = DiscoverConfigurations().Single(c => ConnectorNameOf(c) == connectorName);

        BackgroundServiceConfigTypes().Should().Contain(configType,
            "{0} carries [ConnectorRegistration] but no ConnectorBackgroundService<{1}> exists in " +
            "the API assembly, so AddConnectors registers no hosted service for it and it never " +
            "syncs",
            connectorName, configType.Name);
    }

    [Fact]
    public void AllowlistedConnectors_StillExistAndStillHaveNoBackgroundService()
    {
        var known = DiscoverConnectorNames().ToHashSet(StringComparer.Ordinal);
        var withService = BackgroundServiceConfigTypes()
            .Select(ConnectorNameOf)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var name in NonPollingByDesign.Concat(PollingWithoutASyncServiceYet))
        {
            known.Should().Contain(name,
                "the allowlists name connectors that no longer exist otherwise, which hides the " +
                "next uncovered connector behind a stale entry");

            withService.Should().NotContain(name,
                "{0} now has a background service, so its allowlist entry must go — leaving it " +
                "there exempts a covered connector from the guard",
                name);
        }
    }

    private static List<string> DiscoverConnectorNames() =>
        [.. DiscoverConfigurations().Select(ConnectorNameOf).Where(n => n is not null).Distinct()!];

    private static List<Type> DiscoverConfigurations()
    {
        // Touch one type per connector assembly so they are loaded before the scan.
        _ = typeof(ConnectorRegistrationAttribute);
        foreach (var path in Directory.GetFiles(
                     AppContext.BaseDirectory, "Nocturne.Connectors.*.dll"))
        {
            try
            {
                Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly; nothing to scan.
            }
        }

        return [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Nocturne.Connectors.", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && t.GetCustomAttribute<ConnectorRegistrationAttribute>() is not null)
            .Distinct()];
    }

    /// <summary>
    /// The <c>TConfig</c> of every <see cref="ConnectorBackgroundService{TConfig}"/> subclass in the
    /// API assembly, matched the way <c>AddConnectors</c> matches them.
    /// </summary>
    private static List<Type> BackgroundServiceConfigTypes() =>
        [.. SafeTypes(typeof(ConnectorBackgroundService<>).Assembly)
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Select(t => t.BaseType)
            .Where(b => b is { IsGenericType: true }
                        && b.GetGenericTypeDefinition() == typeof(ConnectorBackgroundService<>))
            .Select(b => b!.GetGenericArguments()[0])
            .Distinct()];

    private static string? ConnectorNameOf(Type configType) =>
        configType.GetCustomAttribute<ConnectorRegistrationAttribute>()?.ConnectorName;

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}

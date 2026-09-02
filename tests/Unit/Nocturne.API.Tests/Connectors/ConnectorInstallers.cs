using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Finds the shipped connectors by reflection. The connector projects are references of this test
/// project without any test naming their types, so nothing has loaded them by the time a test asks
/// what connectors exist — they have to be pulled off disk first.
/// </summary>
internal static class ConnectorInstallers
{
    private static readonly ConcurrentDictionary<Type, string> NameCache = new();

    /// <summary>
    /// One installer per connector, discovered rather than listed so a new connector project is
    /// covered without editing a test.
    /// </summary>
    internal static List<IConnectorInstaller> Discover() =>
        [.. Types()
            .Where(t => typeof(IConnectorInstaller).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .DistinctBy(t => t.FullName)
            .Select(t => (IConnectorInstaller)Activator.CreateInstance(t)!)];

    /// <summary>
    /// The connector name an installer keys its configuration by, taken from the frozen startup
    /// registration every installer makes through <c>AddConnectorConfiguration</c>. That is the same
    /// name <see cref="IConnectorConfigurationLoader{TConfig}"/> reads per-tenant configuration and
    /// secrets under, so it cannot drift from what the connector actually runs as.
    /// </summary>
    /// <remarks>
    /// Read by installing into a throwaway collection, because an installer does not state its own
    /// name — nothing in production asks one for it. Covariance on
    /// <see cref="IConnectorRegistration{TConfig}"/> is what lets the closed registration be read
    /// without knowing the configuration type.
    /// </remarks>
    internal static string NameOf(IConnectorInstaller installer) =>
        NameCache.GetOrAdd(installer.GetType(), _ =>
        {
            var services = new ServiceCollection();
            installer.Install(services, new ConfigurationBuilder().Build());

            return services
                .Select(descriptor => descriptor.ImplementationInstance)
                .OfType<IConnectorRegistration<BaseConnectorConfiguration>>()
                .Select(registration => registration.ConnectorName)
                .Single();
        });

    /// <summary>
    /// The closed <paramref name="openBase"/> in <paramref name="installer"/>'s inheritance chain,
    /// or <c>null</c> when it derives from no such thing.
    /// </summary>
    internal static Type? ClosedBaseOf(Type installer, Type openBase)
    {
        for (var current = installer; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openBase)
                return current;

        return null;
    }

    /// <summary>
    /// Every type across the connector assemblies.
    /// </summary>
    internal static IEnumerable<Type> Types()
    {
        // Touch one type per connector assembly so they are loaded before the scan.
        _ = typeof(IConnectorInstaller);
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "Nocturne.Connectors.*.dll"))
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

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Nocturne.Connectors.", StringComparison.Ordinal) == true)
            .SelectMany(SafeTypes);
    }

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

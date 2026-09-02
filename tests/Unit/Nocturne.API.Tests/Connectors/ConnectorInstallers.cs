using System.Reflection;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Finds the shipped connectors by reflection. The connector projects are references of this test
/// project without any test naming their types, so nothing has loaded them by the time a test asks
/// what connectors exist — they have to be pulled off disk first.
/// </summary>
internal static class ConnectorInstallers
{
    /// <summary>
    /// One installer per connector, discovered rather than listed so a new connector project is
    /// covered without editing a test.
    /// </summary>
    internal static List<IConnectorInstaller> Discover() =>
        [.. Types()
            .Where(t => typeof(IConnectorInstaller).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IConnectorInstaller)Activator.CreateInstance(t)!)
            .GroupBy(i => i.ConnectorName)
            .Select(g => g.First())];

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

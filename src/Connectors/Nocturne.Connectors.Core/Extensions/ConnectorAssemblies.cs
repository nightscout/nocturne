using System.Reflection;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     The one place that decides which assemblies a connector scan reads. The host references no
///     connector assembly directly, and the runtime loads one only when a type of it is first used, so
///     a scan of what happens to be loaded finds a different set depending on what ran before it.
/// </summary>
public static class ConnectorAssemblies
{
    /// <summary>
    ///     Loads every <c>Nocturne.Connectors.*.dll</c> of the app's base directory that is not loaded
    ///     yet, then returns every loaded assembly of the connector namespace.
    /// </summary>
    public static IReadOnlyList<Assembly> Load()
    {
        foreach (var dll in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nocturne.Connectors.*.dll"))
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(dll);
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .All(a => a.GetName().Name != assemblyName.Name))
                {
                    Assembly.LoadFrom(dll);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Not a loadable managed assembly; nothing in it can register a connector.
            }
        }

        return [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Nocturne.Connectors") == true)];
    }
}

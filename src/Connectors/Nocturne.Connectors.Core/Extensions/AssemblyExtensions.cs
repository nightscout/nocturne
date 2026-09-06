using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Nocturne.Connectors.Core.Extensions;

public static class AssemblyExtensions
{
    extension(Assembly assembly)
    {
        /// <summary>
        ///     The assembly's types, less any that cannot be loaded — a missing transitive
        ///     dependency, a version mismatch after an image bump. One such type fails
        ///     <see cref="Assembly.GetTypes"/> for every type in the assembly, so a scan that treats
        ///     the failure as "no types here" loses every connector the assembly ships.
        /// </summary>
        public IEnumerable<Type> LoadableTypes(ILogger? logger = null)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.LoadableTypes(assembly.GetName().Name, logger);
            }
        }
    }

    extension(ReflectionTypeLoadException exception)
    {
        /// <summary>
        ///     The types a failed <see cref="Assembly.GetTypes"/> did load, with the loader failures
        ///     reported against <paramref name="assemblyName"/>.
        /// </summary>
        public IEnumerable<Type> LoadableTypes(string? assemblyName, ILogger? logger = null)
        {
            logger?.LogWarning(
                "Types in {ConnectorAssembly} failed to load; only the loadable types are scanned: "
                + "{LoaderErrors}",
                assemblyName,
                string.Join("; ", exception.LoaderExceptions.Select(loaderError => loaderError?.Message)));

            return exception.Types.OfType<Type>();
        }
    }
}

using System.Reflection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Provides connector display metadata from ConnectorRegistration attributes.
/// </summary>
public static class ConnectorMetadataService
{
    private static readonly Dictionary<string, ConnectorDisplayInfo> ConnectorsByDataSourceId = new();
    private static readonly Dictionary<string, ConnectorRegistrationAttribute> RegistrationsByConnectorId = new();
    private static bool _initialized;
    private static readonly Lock Lock = new();

    /// <summary>
    ///     Gets connector display info by DataSource ID (e.g., "dexcom-connector").
    ///     Returns null if the dataSourceId is not from a known connector.
    /// </summary>
    public static ConnectorDisplayInfo? GetByDataSourceId(string? dataSourceId)
    {
        if (string.IsNullOrEmpty(dataSourceId))
            return null;

        EnsureInitialized();

        ConnectorsByDataSourceId.TryGetValue(dataSourceId, out var info);
        return info;
    }

    /// <summary>
    ///     Gets connector display info by Connector ID (name) (e.g., "dexcom").
    ///     Returns null if the connectorId is not found.
    /// </summary>
    public static ConnectorDisplayInfo? GetByConnectorId(string? connectorId)
    {
        if (string.IsNullOrEmpty(connectorId))
            return null;

        EnsureInitialized();

        return ConnectorsByDataSourceId.Values.FirstOrDefault(c =>
            c.ConnectorName.Equals(connectorId, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    ///     Gets connector registration info by Connector ID (name) (e.g., "dexcom").
    /// </summary>
    public static ConnectorRegistrationAttribute? GetRegistrationByConnectorId(string? connectorId)
    {
        if (string.IsNullOrEmpty(connectorId))
            return null;

        EnsureInitialized();

        RegistrationsByConnectorId.TryGetValue(connectorId.ToLowerInvariant(), out var registration);
        return registration;
    }

    /// <summary>
    ///     Gets all registered connector display info.
    /// </summary>
    public static IReadOnlyCollection<ConnectorDisplayInfo> GetAll()
    {
        EnsureInitialized();
        return ConnectorsByDataSourceId.Values.ToList().AsReadOnly();
    }

    /// <summary>
    ///     Gets all connectors as AvailableService objects for UI consumption.
    /// </summary>
    public static List<AvailableService> GetAvailableServices()
    {
        EnsureInitialized();
        return ConnectorsByDataSourceId.Values
            .Select(c => c.ToAvailableService())
            .ToList();
    }

    /// <summary>
    ///     Checks if a dataSourceId is from a connector (vs uploader, manual entry, etc.)
    /// </summary>
    public static bool IsConnectorDataSource(string? dataSourceId)
    {
        if (string.IsNullOrEmpty(dataSourceId))
            return false;

        EnsureInitialized();
        return ConnectorsByDataSourceId.ContainsKey(dataSourceId);
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (Lock)
        {
            if (_initialized)
                return;

            // Scan all loaded assemblies for ConnectorRegistration attributes
            // Connector assemblies are loaded through normal DI registration (AddXxxConnector methods)
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.FullName?.Contains("Nocturne.Connectors") == true)
                .ToList();

            foreach (var assembly in assemblies)
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        var attr = type.GetCustomAttribute<ConnectorRegistrationAttribute>();
                        if (attr == null || string.IsNullOrEmpty(attr.DataSourceId)) continue;
                        var info = new ConnectorDisplayInfo
                        {
                            ConnectorName = attr.ConnectorName,
                            DisplayName = attr.DisplayName,
                            DataSourceId = attr.DataSourceId,
                            Icon = attr.Icon,
                            Category = attr.Category,
                            Description = attr.Description,
                            ServiceName = attr.ServiceName
                        };

                        ConnectorsByDataSourceId[attr.DataSourceId] = info;

                        var connectorId = attr.ConnectorName.ToLowerInvariant();
                        RegistrationsByConnectorId[connectorId] = attr;
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // Some types may not be loadable, skip them
                }

            _initialized = true;
        }
    }

    /// <summary>
    ///     Connector display information extracted from ConnectorRegistration attribute.
    /// </summary>
    public class ConnectorDisplayInfo
    {
        public string ConnectorName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string DataSourceId { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public ConnectorCategory Category { get; init; } = ConnectorCategory.Other;
        public string Description { get; init; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;

        /// <summary>
        ///     Converts this connector info to an AvailableService for UI consumption.
        /// </summary>
        public AvailableService ToAvailableService()
        {
            return new AvailableService
            {
                Id = ConnectorName,
                Name = DisplayName,
                Type = Category.ToString().ToLowerInvariant(),
                Description = Description,
                Icon = Icon
            };
        }
    }
}

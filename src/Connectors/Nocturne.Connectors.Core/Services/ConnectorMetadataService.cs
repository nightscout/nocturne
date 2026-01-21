using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models.Configuration;

#nullable enable

namespace Nocturne.Connectors.Core.Services
{
    /// <summary>
    /// Provides connector display metadata from ConnectorRegistration attributes.
    /// </summary>
    public static class ConnectorMetadataService
    {
        private static readonly Dictionary<string, ConnectorDisplayInfo> _connectorsByDataSourceId = new();
        private static bool _initialized = false;
        private static readonly object _lock = new();

        /// <summary>
        /// Connector display information extracted from ConnectorRegistration attribute.
        /// </summary>
        public class ConnectorDisplayInfo
        {
            public string ConnectorName { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string DataSourceId { get; set; } = string.Empty;
            public string Icon { get; set; } = string.Empty;
            public ConnectorCategory Category { get; set; } = ConnectorCategory.Other;
            public string Description { get; set; } = string.Empty;
            public string ServiceName { get; set; } = string.Empty;

            /// <summary>
            /// Converts this connector info to an AvailableService for UI consumption.
            /// </summary>
            public AvailableService ToAvailableService()
            {
                // Use connector name as the service ID (e.g., "glooko" not "glooko-connector")
                var serviceId = ConnectorName.ToLowerInvariant();

                return new AvailableService
                {
                    Id = serviceId,
                    Name = DisplayName,
                    Type = Category.ToString().ToLowerInvariant(),
                    Description = Description,
                    Icon = Icon
                };
            }
        }

        /// <summary>
        /// Gets connector display info by DataSource ID (e.g., "dexcom-connector").
        /// Returns null if the dataSourceId is not from a known connector.
        /// </summary>
        public static ConnectorDisplayInfo? GetByDataSourceId(string? dataSourceId)
        {
            if (string.IsNullOrEmpty(dataSourceId))
                return null;

            EnsureInitialized();

            _connectorsByDataSourceId.TryGetValue(dataSourceId, out var info);
            return info;
        }

        /// <summary>
        /// Gets connector display info by Connector ID (name) (e.g., "dexcom").
        /// Returns null if the connectorId is not found.
        /// </summary>
        public static ConnectorDisplayInfo? GetByConnectorId(string? connectorId)
        {
            if (string.IsNullOrEmpty(connectorId))
                return null;

            EnsureInitialized();

            return _connectorsByDataSourceId.Values.FirstOrDefault(c =>
                c.ConnectorName.Equals(connectorId, StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// Gets all registered connector display info.
        /// </summary>
        public static IReadOnlyCollection<ConnectorDisplayInfo> GetAll()
        {
            EnsureInitialized();
            return _connectorsByDataSourceId.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets all connectors as AvailableService objects for UI consumption.
        /// </summary>
        public static List<AvailableService> GetAvailableServices()
        {
            EnsureInitialized();
            return _connectorsByDataSourceId.Values
                .Select(c => c.ToAvailableService())
                .ToList();
        }

        /// <summary>
        /// Checks if a dataSourceId is from a connector (vs uploader, manual entry, etc.)
        /// </summary>
        public static bool IsConnectorDataSource(string? dataSourceId)
        {
            if (string.IsNullOrEmpty(dataSourceId))
                return false;

            EnsureInitialized();
            return _connectorsByDataSourceId.ContainsKey(dataSourceId);
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (_lock)
            {
                if (_initialized)
                    return;

                // Force load the Configurations assembly if not already loaded
                // .NET loads assemblies lazily, so we need to ensure it's loaded before scanning
                try
                {
                    Assembly.Load("Nocturne.Connectors.Configurations");
                }
                catch
                {
                    // Assembly may not be available in all contexts
                }

                // Scan all loaded assemblies for ConnectorRegistration attributes
                var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.FullName?.Contains("Nocturne.Connectors") == true)
                    .ToList();

                foreach (var assembly in assemblies)
                {
                    try
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            var attr = type.GetCustomAttribute<ConnectorRegistrationAttribute>();
                            if (attr != null && !string.IsNullOrEmpty(attr.DataSourceId))
                            {
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

                                _connectorsByDataSourceId[attr.DataSourceId] = info;
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // Some types may not be loadable, skip them
                    }
                }

                _initialized = true;
            }
        }

        /// <summary>
        /// Force re-initialization (useful for testing).
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _connectorsByDataSourceId.Clear();
                _initialized = false;
            }
        }
    }
}


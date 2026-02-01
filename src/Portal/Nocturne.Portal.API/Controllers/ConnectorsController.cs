using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Portal.API.Models;

namespace Nocturne.Portal.API.Controllers;

/// <summary>
/// Provides connector metadata for the configuration wizard
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ConnectorsController : ControllerBase
{
    /// <summary>
    /// Returns metadata for all available connectors including their configuration fields
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ConnectorsResponse), StatusCodes.Status200OK)]
    public ActionResult<ConnectorsResponse> GetConnectors()
    {
        var connectors = new List<ConnectorMetadataDto>();

        // Ensure connector assemblies are loaded, then scan for configuration classes
        var connectorAssemblies = new[]
        {
            "Nocturne.Connectors.Dexcom",
            "Nocturne.Connectors.Glooko",
            "Nocturne.Connectors.FreeStyle",
            "Nocturne.Connectors.MyLife"
        };

        foreach (var name in connectorAssemblies)
        {
            try
            {
                Assembly.Load(name);
            }
            catch
            {
                // Assembly may not be available in all contexts
            }
        }

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Nocturne.Connectors.") == true)
            .ToList();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                var registrationAttr = type.GetCustomAttribute<ConnectorRegistrationAttribute>();
                if (registrationAttr == null)
                    continue;

                var connector = new ConnectorMetadataDto
                {
                    Type = registrationAttr.ConnectorName,
                    DisplayName = registrationAttr.DisplayName,
                    Category = registrationAttr.Category.ToString(),
                    Description = registrationAttr.Description,
                    Icon = registrationAttr.Icon,
                    Fields = GetConnectorFields(type)
                };

                connectors.Add(connector);
            }
        }

        return Ok(new ConnectorsResponse { Connectors = connectors });
    }

    private static List<ConnectorFieldDto> GetConnectorFields(Type configType)
    {
        var fields = new List<ConnectorFieldDto>();

        foreach (var property in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var aspireAttr = property.GetCustomAttribute<AspireParameterAttribute>();
            var envVarAttr = property.GetCustomAttribute<EnvironmentVariableAttribute>();
            var requiredAttr = property.GetCustomAttribute<RequiredAttribute>();

            // Only include properties that have Aspire or EnvironmentVariable attributes
            if (aspireAttr == null && envVarAttr == null)
                continue;

            var field = new ConnectorFieldDto
            {
                Name = property.Name,
                EnvVar = envVarAttr?.Name ?? aspireAttr?.ParameterName ?? property.Name,
                Type = GetFieldType(property, aspireAttr),
                Required = requiredAttr != null,
                Description = aspireAttr?.Description ?? string.Empty,
                Default = aspireAttr?.DefaultValue
            };

            // Detect select-type fields for server/region properties
            if (property.Name.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Region", StringComparison.OrdinalIgnoreCase))
            {
                field.Type = "select";
                field.Options = property.Name.Contains("Region", StringComparison.OrdinalIgnoreCase)
                    ? ["US", "EU", "AU", "JP"]
                    : ["US", "EU"];
            }

            fields.Add(field);
        }

        return fields;
    }

    private static string GetFieldType(PropertyInfo property, AspireParameterAttribute? aspireAttr)
    {
        if (aspireAttr?.IsSecret == true)
            return "password";

        if (property.PropertyType == typeof(bool))
            return "boolean";

        if (property.PropertyType == typeof(int) || property.PropertyType == typeof(double))
            return "number";

        return "string";
    }
}

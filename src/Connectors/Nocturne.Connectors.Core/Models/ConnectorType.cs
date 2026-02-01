using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Defines the implementation type of the connector project.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorType
{
    /// <summary>
    ///     Standard .NET Aspire Project (Default)
    /// </summary>
    CSharpProject,

    /// <summary>
    ///     Python Application (e.g., FastAPI/Uvicorn)
    /// </summary>
    PythonApp
}
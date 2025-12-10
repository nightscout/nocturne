using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers;

/// <summary>
/// Metadata controller that exposes type definitions for frontend clients
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MetadataController : ControllerBase
{
    /// <summary>
    /// Get WebSocket event types metadata
    /// This endpoint exists primarily to ensure NSwag generates TypeScript types for WebSocket events
    /// </summary>
    /// <returns>WebSocket events metadata</returns>
    [HttpGet("websocket-events")]
    [ProducesResponseType(typeof(WebSocketEventsMetadata), 200)]
    public ActionResult<WebSocketEventsMetadata> GetWebSocketEvents()
    {
        return Ok(
            new WebSocketEventsMetadata
            {
                AvailableEvents = Enum.GetValues<WebSocketEvents>(),
                Description = "Available WebSocket event types for real-time communication",
            }
        );
    }

    /// <summary>
    /// Get external URLs for documentation and website
    /// This endpoint provides a single source of truth for all external Nocturne URLs
    /// </summary>
    /// <returns>External URLs configuration</returns>
    [HttpGet("external-urls")]
    [ProducesResponseType(typeof(ExternalUrls), 200)]
    public ActionResult<ExternalUrls> GetExternalUrls()
    {
        return Ok(
            new ExternalUrls
            {
                Website = UrlConstants.External.NocturneWebsite,
                DocsBase = UrlConstants.External.NocturneDocsBase,
                ConnectorDocs = new ConnectorDocsUrls
                {
                    Dexcom = UrlConstants.External.DocsDexcom,
                    Libre = UrlConstants.External.DocsLibre,
                    CareLink = UrlConstants.External.DocsCareLink,
                    Nightscout = UrlConstants.External.DocsNightscout,
                    Glooko = UrlConstants.External.DocsGlooko,
                },
            }
        );
    }
    /// <summary>
    /// Get treatment event types metadata
    /// This endpoint exposes all available treatment event types for type-safe usage in frontend clients
    /// </summary>
    /// <returns>Treatment event types metadata</returns>
    [HttpGet("treatment-event-types")]
    [ProducesResponseType(typeof(TreatmentEventTypesMetadata), 200)]
    public ActionResult<TreatmentEventTypesMetadata> GetTreatmentEventTypes()
    {
        return Ok(
            new TreatmentEventTypesMetadata
            {
                AvailableTypes = Enum.GetValues<TreatmentEventType>(),
                Configurations = EventTypeConfigurations.GetAll(),
                Description = "Available treatment event types for diabetes management events",
            }
        );
    }
}

/// <summary>
/// Metadata about available WebSocket events
/// </summary>
public class WebSocketEventsMetadata
{
    /// <summary>
    /// Array of all available WebSocket event types
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WebSocketEvents[] AvailableEvents { get; set; } = [];

    /// <summary>
    /// Description of the WebSocket events
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about available treatment event types
/// </summary>
public class TreatmentEventTypesMetadata
{
    /// <summary>
    /// Array of all available treatment event types
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TreatmentEventType[] AvailableTypes { get; set; } = [];

    /// <summary>
    /// Full configurations for each event type including field applicability
    /// </summary>
    public EventTypeConfiguration[] Configurations { get; set; } = [];

    /// <summary>
    /// Description of the treatment event types
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

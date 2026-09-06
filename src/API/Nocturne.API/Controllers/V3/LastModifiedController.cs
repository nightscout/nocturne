using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using System.Linq;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// LastModified controller that provides timestamps for when collections were last modified.
/// Used by clients (particularly AAPS) to determine which collections need syncing.
/// </summary>
/// <seealso cref="IStatusService"/>
/// <seealso cref="LastModifiedResponse"/>
[ApiController]
[Tags("V3")]
[Route("api/v3/[controller]")]
[AllowAnonymous]
public class LastModifiedController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly ILogger<LastModifiedController> _logger;

    public LastModifiedController(
        IStatusService statusService,
        ILogger<LastModifiedController> logger
    )
    {
        _statusService = statusService;
        _logger = logger;
    }

    /// <summary>
    /// Get last modified timestamps for all collections.
    /// </summary>
    /// <returns>A <see cref="LastModifiedResponse"/> with per-collection timestamps and server time.</returns>
    /// <remarks>
    /// On error, returns a minimal <see cref="LastModifiedResponse"/> with null timestamps
    /// to maintain compatibility with clients that depend on this endpoint always returning 200.
    /// </remarks>
    /// <response code="200">Last modified timestamps for each collection.</response>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/lastModified")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<ActionResult> GetLastModified()
    {
        _logger.LogDebug(
            "LastModified endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var lastModified = await _statusService.GetLastModifiedAsync();

            _logger.LogDebug("Successfully generated last modified response");

            return Ok(ToV3Envelope(lastModified));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating last modified response");

            // Return the envelope even on error to maintain compatibility with clients
            // (e.g. AAPS) that depend on this endpoint always returning the V3 shape.
            return Ok(ToV3Envelope(new LastModifiedResponse { ServerTime = DateTime.UtcNow }));
        }
    }

    /// <summary>
    /// Convert a <see cref="LastModifiedResponse"/> to the Nightscout V3 envelope AAPS expects:
    /// <c>{ status, result: { srvDate, collections } }</c> with Unix-millisecond timestamps and
    /// lowercase collection keys (singular <c>profile</c>, lowercase <c>devicestatus</c>).
    /// </summary>
    private static object ToV3Envelope(LastModifiedResponse lastModified)
    {
        var collections = new Dictionary<string, long>();

        static long ToUnixMillis(DateTime value) =>
            new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        void Add(string key, DateTime? value)
        {
            if (value.HasValue)
                collections[key] = ToUnixMillis(value.Value);
        }

        Add("entries", lastModified.Entries);
        Add("treatments", lastModified.Treatments);
        Add("profile", lastModified.Profile);
        Add("devicestatus", lastModified.DeviceStatus);
        Add("food", lastModified.Food);
        Add("settings", lastModified.Settings);
        Add("activity", lastModified.Activity);

        foreach (var kvp in lastModified.Additional.Where(kvp => kvp.Key != "auth"))
        {
            // Exclude internal keys (e.g. auth) that are not sync collections.
            Add(kvp.Key, kvp.Value);
        }

        return new
        {
            status = 200,
            result = new
            {
                srvDate = ToUnixMillis(lastModified.ServerTime),
                collections,
            },
        };
    }
}

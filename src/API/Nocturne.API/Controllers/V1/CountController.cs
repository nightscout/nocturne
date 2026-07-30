using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Authorization;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Count controller that provides 1:1 compatibility with Nightscout count endpoints.
/// Implements the /api/v1/count/* endpoints from the legacy JavaScript implementation.
/// </summary>
/// <seealso cref="IEntryStore"/>
/// <seealso cref="ITreatmentStore"/>
/// <seealso cref="IApsSnapshotRepository"/>
/// <seealso cref="IProfileProjectionService"/>
/// <seealso cref="IFoodRepository"/>
/// <seealso cref="IActivityService"/>
[ApiController]
[Tags("V1")]
[Route("api/v1/[controller]")]
public class CountController : ControllerBase
{
    private readonly IEntryStore _entryStore;
    private readonly ITreatmentStore _treatmentStore;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly IProfileProjectionService _profileProjectionService;
    private readonly IFoodRepository _foodRepository;
    private readonly IActivityService _activityService;
    private readonly ILogger<CountController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CountController"/>.
    /// </summary>
    /// <param name="entryStore">Store for glucose entry records.</param>
    /// <param name="treatmentStore">Store for treatment records.</param>
    /// <param name="apsSnapshotRepository">Repository for APS snapshot records (V4 replacement for device status).</param>
    /// <param name="profileProjectionService">Service for profile projection and counting.</param>
    /// <param name="foodRepository">Repository for food records.</param>
    /// <param name="activityService">Service for activity operations.</param>
    /// <param name="logger">Logger instance.</param>
    public CountController(
        IEntryStore entryStore,
        ITreatmentStore treatmentStore,
        IApsSnapshotRepository apsSnapshotRepository,
        IProfileProjectionService profileProjectionService,
        IFoodRepository foodRepository,
        IActivityService activityService,
        ILogger<CountController> logger
    )
    {
        _entryStore = entryStore;
        _treatmentStore = treatmentStore;
        _apsSnapshotRepository = apsSnapshotRepository;
        _profileProjectionService = profileProjectionService;
        _foodRepository = foodRepository;
        _activityService = activityService;
        _logger = logger;
    }

    /// <summary>
    /// Count entries matching specific criteria
    /// </summary>
    /// <param name="find">MongoDB-style find query filters (JSON format)</param>
    /// <param name="type">Entry type filter (sgv, mbg, cal)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of entries matching the criteria</returns>
    [HttpGet("entries/where")]
    [NightscoutEndpoint("/api/v1/count/entries/where")]
    [ProducesResponseType(typeof(CountResponse), 200)]
    [RequireScope(OAuthScopes.GlucoseRead)]
    public async Task<ActionResult<CountResponse>> CountEntries(
        [FromQuery] string? find = null,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Count entries endpoint requested with find: {Find}, type: {Type} from {RemoteIpAddress}",
            find,
            type,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var count = await _entryStore.CountAsync(find, type, cancellationToken);

            _logger.LogDebug("Found {Count} entries matching criteria", count);
            return Ok(new CountResponse { Count = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error counting entries with find: {Find}, type: {Type}",
                find,
                type
            );
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error while counting entries",
                    type = "internal",
                }
            );
        }
    }

    /// <summary>
    /// Count treatments matching specific criteria
    /// </summary>
    /// <param name="find">MongoDB-style find query filters (JSON format)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of treatments matching the criteria</returns>
    [HttpGet("treatments/where")]
    [NightscoutEndpoint("/api/v1/count/treatments/where")]
    [ProducesResponseType(typeof(CountResponse), 200)]
    [RequireScope(OAuthScopes.TreatmentsRead)]
    public async Task<ActionResult<CountResponse>> CountTreatments(
        [FromQuery] string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Count treatments endpoint requested with find: {Find} from {RemoteIpAddress}",
            find,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var count = await _treatmentStore.CountAsync(find, cancellationToken);

            _logger.LogDebug("Found {Count} treatments matching criteria", count);
            return Ok(new CountResponse { Count = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting treatments with find: {Find}", find);
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error while counting treatments",
                    type = "internal",
                }
            );
        }
    }

    /// <summary>
    /// Count device status entries matching specific criteria
    /// </summary>
    /// <param name="find">MongoDB-style find query filters (JSON format)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of device status entries matching the criteria</returns>
    [HttpGet("devicestatus/where")]
    [NightscoutEndpoint("/api/v1/count/devicestatus/where")]
    [ProducesResponseType(typeof(CountResponse), 200)]
    [RequireScope(OAuthScopes.DevicesRead)]
    public async Task<ActionResult<CountResponse>> CountDeviceStatus(
        [FromQuery] string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Count device status endpoint requested with find: {Find} from {RemoteIpAddress}",
            find,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var count = await _apsSnapshotRepository.CountAsync(null, null, cancellationToken);

            _logger.LogDebug("Found {Count} device status entries matching criteria", count);
            return Ok(new CountResponse { Count = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting device status with find: {Find}", find);
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error while counting device status",
                    type = "internal",
                }
            );
        }
    }

    /// <summary>
    /// Count activity entries matching specific criteria
    /// </summary>
    /// <param name="find">MongoDB-style find query filters (JSON format)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of activity entries matching the criteria</returns>
    /// <remarks>
    /// <c>CountActivitiesAsync</c> sums four storages — StateSpans, heart rates, step counts and
    /// sleep sessions — into a single number, so unlike the record-returning activity endpoints
    /// the result cannot be filtered down to the categories the caller holds. The requirement is
    /// therefore every category's read scope (AND), which is what the legacy admin, <c>api:*:read</c>
    /// and <c>readable</c> grants carry. Serving a per-category count needs a source-aware count on
    /// <c>IActivityService</c>.
    /// </remarks>
    [HttpGet("activity/where")]
    [NightscoutEndpoint("/api/v1/count/activity/where")]
    [ProducesResponseType(typeof(CountResponse), 200)]
    public async Task<ActionResult<CountResponse>> CountActivity(
        [FromQuery] string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        // Every category, because the number merges four of them and cannot be filtered down to the
        // ones the caller holds. A grant without sleep.read therefore loses this count; the scopes
        // come from ActivityReadScopeGuard so the set cannot drift from the record endpoints'.
        var granted = HttpContext.GetGrantedScopes();
        if (!ActivityReadScopeGuard.AdmissionScopes.All(s => OAuthScopes.SatisfiesScope(granted, s)))
            return StatusCode(403, new { status = 403, message = "Counting merged activity requires every activity category's read scope.", type = "forbidden" });

        _logger.LogDebug(
            "Count activity endpoint requested with find: {Find} from {RemoteIpAddress}",
            find,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            var count = await _activityService.CountActivitiesAsync(find, cancellationToken);

            _logger.LogDebug("Found {Count} activity entries matching criteria", count);
            return Ok(new CountResponse { Count = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting activity with find: {Find}", find);
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = "Internal server error while counting activity",
                    type = "internal",
                }
            );
        }
    }

    /// <summary>
    /// Generic count endpoint for any storage type</summary>
    /// <param name="storage">Storage type (entries, treatments, devicestatus, profile, food, activity)</param>
    /// <param name="find">MongoDB-style find query filters (JSON format)</param>
    /// <param name="type">Additional type filter (for entries: sgv, mbg, cal)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of records matching the criteria</returns>
    /// <remarks>
    /// The storage is a route parameter, so the governing scope is resolved per request through
    /// <see cref="LegacyStorageReadScopes"/>. An attribute here would be an OR across every
    /// collection the route serves and would let a grant scoped to one category learn a row count
    /// for another. <c>activity</c> merges four categories and keeps the AND below.
    /// </remarks>
    [HttpGet("{storage}/where")]
    [NightscoutEndpoint("/api/v1/count/:storage/where")]
    [ProducesResponseType(typeof(CountResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CountResponse>> CountGeneric(
        string storage,
        [FromQuery] string? find = null,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default
    )
    {
        // "activity" reaches CountActivitiesAsync, which merges four categories into one number, so
        // it needs every category's read scope rather than one storage's.
        if (string.Equals(storage, "activity", StringComparison.OrdinalIgnoreCase))
        {
            var granted = HttpContext.GetGrantedScopes();
            if (!ActivityReadScopeGuard.AdmissionScopes.All(s => OAuthScopes.SatisfiesScope(granted, s)))
                return StatusCode(403, new { status = 403, message = "Counting merged activity requires every activity category's read scope.", type = "forbidden" });
        }
        else if (LegacyStorageReadScopes.RequiredReadScope(storage) is { } required)
        {
            if (!OAuthScopes.SatisfiesScope(HttpContext.GetGrantedScopes(), required))
                return StatusCode(403, new { status = 403, message = $"Counting '{storage}' requires the {required} scope.", type = "forbidden" });
        }

        _logger.LogDebug(
            "Generic count endpoint requested for storage: {Storage}, find: {Find}, type: {Type} from {RemoteIpAddress}",
            storage,
            find,
            type,
            HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown"
        );

        try
        {
            // Validate storage type
            var validStorageTypes = new[]
            {
                "entries",
                "treatments",
                "devicestatus",
                "profile",
                "food",
                "activity",
            };
            if (!validStorageTypes.Contains(storage.ToLowerInvariant()))
            {
                _logger.LogWarning("Invalid storage type requested: {Storage}", storage);
                return BadRequest(
                    new
                    {
                        status = 400,
                        message = $"Invalid storage type: {storage}. Supported types: {string.Join(", ", validStorageTypes)}",
                        type = "client",
                    }
                );
            }

            long count;
            switch (storage.ToLowerInvariant())
            {
                case "entries":
                    count = await _entryStore.CountAsync(
                        find,
                        type,
                        cancellationToken
                    );
                    break;
                case "treatments":
                    count = await _treatmentStore.CountAsync(find, cancellationToken);
                    break;
                case "devicestatus":
                    count = await _apsSnapshotRepository.CountAsync(null, null, cancellationToken);
                    break;
                case "profile":
                    count = await _profileProjectionService.CountProfilesAsync(find, cancellationToken);
                    break;
                case "food":
                    count = await _foodRepository.CountFoodAsync(find, type, cancellationToken);
                    break;
                case "activity":
                    count = await _activityService.CountActivitiesAsync(find, cancellationToken);
                    break;
                default:
                    // This shouldn't happen due to validation above, but just in case
                    return BadRequest(
                        new
                        {
                            status = 400,
                            message = $"Unsupported storage type: {storage}",
                            type = "client",
                        }
                    );
            }

            _logger.LogDebug("Found {Count} {Storage} records matching criteria", count, storage);
            return Ok(new CountResponse { Count = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error counting {Storage} with find: {Find}, type: {Type}",
                storage,
                find,
                type
            );
            return StatusCode(
                500,
                new
                {
                    status = 500,
                    message = $"Internal server error while counting {storage}",
                    type = "internal",
                }
            );
        }
    }
}

/// <summary>
/// Response object for count endpoints
/// </summary>
public class CountResponse
{
    /// <summary>
    /// Number of records matching the query criteria
    /// </summary>
    public long Count { get; set; }
}

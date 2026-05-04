using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Controller for managing V4 profile data: therapy settings, basal schedules,
/// carb ratio schedules, sensitivity schedules, and target range schedules.
/// </summary>
/// <seealso cref="ITherapySettingsRepository"/>
/// <seealso cref="IBasalScheduleRepository"/>
/// <seealso cref="ICarbRatioScheduleRepository"/>
/// <seealso cref="ISensitivityScheduleRepository"/>
/// <seealso cref="ITargetRangeScheduleRepository"/>
[ApiController]
[Route("api/v4/profile")]
[Authorize]
[Produces("application/json")]
public class ProfileController : ControllerBase
{
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IBasalScheduleRepository _basalRepo;
    private readonly ICarbRatioScheduleRepository _carbRatioRepo;
    private readonly ISensitivityScheduleRepository _sensitivityRepo;
    private readonly ITargetRangeScheduleRepository _targetRangeRepo;
    private readonly IProfileProjectionService _projectionService;

    /// <summary>
    /// Initializes a new instance of <see cref="ProfileController"/>.
    /// </summary>
    /// <param name="therapyRepo">Repository for therapy settings records.</param>
    /// <param name="basalRepo">Repository for basal schedule records.</param>
    /// <param name="carbRatioRepo">Repository for carb ratio schedule records.</param>
    /// <param name="sensitivityRepo">Repository for insulin sensitivity schedule records.</param>
    /// <param name="targetRangeRepo">Repository for target glucose range schedule records.</param>
    /// <param name="projectionService">Service for reading legacy profile projections from V4 data.</param>
    public ProfileController(
        ITherapySettingsRepository therapyRepo,
        IBasalScheduleRepository basalRepo,
        ICarbRatioScheduleRepository carbRatioRepo,
        ISensitivityScheduleRepository sensitivityRepo,
        ITargetRangeScheduleRepository targetRangeRepo,
        IProfileProjectionService projectionService
    )
    {
        _therapyRepo = therapyRepo;
        _basalRepo = basalRepo;
        _carbRatioRepo = carbRatioRepo;
        _sensitivityRepo = sensitivityRepo;
        _targetRangeRepo = targetRangeRepo;
        _projectionService = projectionService;
    }

    #region Summary

    /// <summary>
    /// Get a consolidated summary of all profile data across all profile names.
    /// Optionally provide a date range to include schedule change detection info.
    /// </summary>
    [HttpGet("summary")]
    [RemoteQuery]
    [ResponseCache(Duration = 300, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(typeof(ProfileSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProfileSummary>> GetProfileSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default
    )
    {
        var therapySettings = await _therapyRepo.GetAsync(
            null,
            null,
            null,
            null,
            1000,
            0,
            true,
            ct
        );
        var basalSchedules = await _basalRepo.GetAsync(null, null, null, null, 1000, 0, true, ct);
        var carbRatioSchedules = await _carbRatioRepo.GetAsync(
            null,
            null,
            null,
            null,
            1000,
            0,
            true,
            ct
        );
        var sensitivitySchedules = await _sensitivityRepo.GetAsync(
            null,
            null,
            null,
            null,
            1000,
            0,
            true,
            ct
        );
        var targetRangeSchedules = await _targetRangeRepo.GetAsync(
            null,
            null,
            null,
            null,
            1000,
            0,
            true,
            ct
        );

        var summary = new ProfileSummary
        {
            TherapySettings = therapySettings,
            BasalSchedules = basalSchedules,
            CarbRatioSchedules = carbRatioSchedules,
            SensitivitySchedules = sensitivitySchedules,
            TargetRangeSchedules = targetRangeSchedules,
        };

        if (from.HasValue && to.HasValue)
        {
            summary.BasalChanges = ComputeChangeInfo(basalSchedules, from.Value, to.Value);
            summary.CarbRatioChanges = ComputeChangeInfo(carbRatioSchedules, from.Value, to.Value);
            summary.SensitivityChanges = ComputeChangeInfo(
                sensitivitySchedules,
                from.Value,
                to.Value
            );
            summary.TargetRangeChanges = ComputeChangeInfo(
                targetRangeSchedules,
                from.Value,
                to.Value
            );
        }

        return Ok(summary);
    }

    #endregion

    #region Legacy Profile Records

    /// <summary>
    /// Get legacy Nightscout-shaped profile records projected from V4 schedule data.
    /// Intended for connector consumption where the caller needs the monolithic
    /// <see cref="Profile"/> shape (store with basal/carbratio/sens/target arrays).
    /// </summary>
    [HttpGet("records")]
    [ProducesResponseType(typeof(PaginatedResponse<Profile>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<Profile>>> GetProfileRecords(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        var data = await _projectionService.GetProfilesAsync(count: limit, skip: offset, ct: ct);
        var total = (int)await _projectionService.CountProfilesAsync(ct: ct);

        return Ok(
            new PaginatedResponse<Profile>
            {
                Data = data,
                Pagination = new(limit, offset, total),
            }
        );
    }

    #endregion

    #region Therapy Settings

    /// <summary>
    /// Get all therapy settings with optional filtering
    /// </summary>
    [HttpGet("settings")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<TherapySettings>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<TherapySettings>>> GetTherapySettings(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null,
        [FromQuery] string? source = null,
        CancellationToken ct = default
    )
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return BadRequest(
                new { error = $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'." }
            );
        var descending = sort == "timestamp_desc";
        var data = await _therapyRepo.GetAsync(
            from,
            to,
            device,
            source,
            limit,
            offset,
            descending,
            ct
        );
        var total = await _therapyRepo.CountAsync(from, to, ct);
        return Ok(
            new PaginatedResponse<TherapySettings>
            {
                Data = data,
                Pagination = new(limit, offset, total),
            }
        );
    }

    /// <summary>
    /// Get therapy settings by profile name
    /// </summary>
    [HttpGet("settings/by-name/{profileName}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<TherapySettings>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TherapySettings>>> GetTherapySettingsByName(
        string profileName,
        CancellationToken ct = default
    )
    {
        var data = await _therapyRepo.GetByProfileNameAsync(profileName, ct);
        return Ok(data);
    }

    /// <summary>
    /// Get a therapy settings record by ID
    /// </summary>
    [HttpGet("settings/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TherapySettings), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TherapySettings>> GetTherapySettingsById(
        Guid id,
        CancellationToken ct = default
    )
    {
        var result = await _therapyRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new therapy settings record
    /// </summary>
    [HttpPost("settings")]
    [RemoteForm(Invalidates = ["GetProfileSummary", "GetTherapySettings"])]
    [ProducesResponseType(typeof(TherapySettings), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TherapySettings>> CreateTherapySettings(
        [FromBody] TherapySettings model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        var created = await _therapyRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetTherapySettingsById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing therapy settings record
    /// </summary>
    [HttpPut("settings/{id:guid}")]
    [RemoteForm(
        Invalidates = ["GetProfileSummary", "GetTherapySettings", "GetTherapySettingsById"]
    )]
    [ProducesResponseType(typeof(TherapySettings), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TherapySettings>> UpdateTherapySettings(
        Guid id,
        [FromBody] TherapySettings model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        try
        {
            var updated = await _therapyRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a therapy settings record
    /// </summary>
    [HttpDelete("settings/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetTherapySettings"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteTherapySettings(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _therapyRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    #region Basal Schedules

    /// <summary>
    /// Get basal schedules by profile name
    /// </summary>
    [HttpGet("basal/{profileName}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<BasalSchedule>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<BasalSchedule>>> GetBasalSchedulesByName(
        string profileName,
        CancellationToken ct = default
    )
    {
        var data = await _basalRepo.GetByProfileNameAsync(profileName, ct);
        return Ok(data);
    }

    /// <summary>
    /// Get a basal schedule by ID
    /// </summary>
    [HttpGet("basal/by-id/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(BasalSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BasalSchedule>> GetBasalScheduleById(
        Guid id,
        CancellationToken ct = default
    )
    {
        var result = await _basalRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new basal schedule
    /// </summary>
    [HttpPost("basal")]
    [RemoteForm(Invalidates = ["GetProfileSummary", "GetBasalSchedulesByName"])]
    [ProducesResponseType(typeof(BasalSchedule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BasalSchedule>> CreateBasalSchedule(
        [FromBody] BasalSchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        var created = await _basalRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetBasalScheduleById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing basal schedule
    /// </summary>
    [HttpPut("basal/{id:guid}")]
    [RemoteForm(
        Invalidates = ["GetProfileSummary", "GetBasalSchedulesByName", "GetBasalScheduleById"]
    )]
    [ProducesResponseType(typeof(BasalSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BasalSchedule>> UpdateBasalSchedule(
        Guid id,
        [FromBody] BasalSchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        try
        {
            var updated = await _basalRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a basal schedule
    /// </summary>
    [HttpDelete("basal/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetBasalSchedulesByName"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteBasalSchedule(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _basalRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    #region Carb Ratio Schedules

    /// <summary>
    /// Get carb ratio schedules by profile name
    /// </summary>
    [HttpGet("carb-ratio/{profileName}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<CarbRatioSchedule>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CarbRatioSchedule>>> GetCarbRatioSchedulesByName(
        string profileName,
        CancellationToken ct = default
    )
    {
        var data = await _carbRatioRepo.GetByProfileNameAsync(profileName, ct);
        return Ok(data);
    }

    /// <summary>
    /// Get a carb ratio schedule by ID
    /// </summary>
    [HttpGet("carb-ratio/by-id/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(CarbRatioSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CarbRatioSchedule>> GetCarbRatioScheduleById(
        Guid id,
        CancellationToken ct = default
    )
    {
        var result = await _carbRatioRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new carb ratio schedule
    /// </summary>
    [HttpPost("carb-ratio")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetCarbRatioSchedulesByName"])]
    [ProducesResponseType(typeof(CarbRatioSchedule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CarbRatioSchedule>> CreateCarbRatioSchedule(
        [FromBody] CarbRatioSchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        var created = await _carbRatioRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetCarbRatioScheduleById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing carb ratio schedule
    /// </summary>
    [HttpPut("carb-ratio/{id:guid}")]
    [RemoteCommand(
        Invalidates = [
            "GetProfileSummary",
            "GetCarbRatioSchedulesByName",
            "GetCarbRatioScheduleById",
        ]
    )]
    [ProducesResponseType(typeof(CarbRatioSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CarbRatioSchedule>> UpdateCarbRatioSchedule(
        Guid id,
        [FromBody] CarbRatioSchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        try
        {
            var updated = await _carbRatioRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a carb ratio schedule
    /// </summary>
    [HttpDelete("carb-ratio/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetCarbRatioSchedulesByName"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteCarbRatioSchedule(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _carbRatioRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    #region Sensitivity Schedules

    /// <summary>
    /// Get sensitivity schedules by profile name
    /// </summary>
    [HttpGet("sensitivity/{profileName}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<SensitivitySchedule>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SensitivitySchedule>>> GetSensitivitySchedulesByName(
        string profileName,
        CancellationToken ct = default
    )
    {
        var data = await _sensitivityRepo.GetByProfileNameAsync(profileName, ct);
        return Ok(data);
    }

    /// <summary>
    /// Get a sensitivity schedule by ID
    /// </summary>
    [HttpGet("sensitivity/by-id/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(SensitivitySchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SensitivitySchedule>> GetSensitivityScheduleById(
        Guid id,
        CancellationToken ct = default
    )
    {
        var result = await _sensitivityRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new sensitivity schedule
    /// </summary>
    [HttpPost("sensitivity")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetSensitivitySchedulesByName"])]
    [ProducesResponseType(typeof(SensitivitySchedule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SensitivitySchedule>> CreateSensitivitySchedule(
        [FromBody] SensitivitySchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        var created = await _sensitivityRepo.CreateAsync(model, ct);
        return CreatedAtAction(
            nameof(GetSensitivityScheduleById),
            new { id = created.Id },
            created
        );
    }

    /// <summary>
    /// Update an existing sensitivity schedule
    /// </summary>
    [HttpPut("sensitivity/{id:guid}")]
    [RemoteCommand(
        Invalidates = [
            "GetProfileSummary",
            "GetSensitivitySchedulesByName",
            "GetSensitivityScheduleById",
        ]
    )]
    [ProducesResponseType(typeof(SensitivitySchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SensitivitySchedule>> UpdateSensitivitySchedule(
        Guid id,
        [FromBody] SensitivitySchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        try
        {
            var updated = await _sensitivityRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a sensitivity schedule
    /// </summary>
    [HttpDelete("sensitivity/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetSensitivitySchedulesByName"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSensitivitySchedule(
        Guid id,
        CancellationToken ct = default
    )
    {
        try
        {
            await _sensitivityRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    #region Target Range Schedules

    /// <summary>
    /// Get target range schedules by profile name
    /// </summary>
    [HttpGet("target-range/{profileName}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<TargetRangeSchedule>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TargetRangeSchedule>>> GetTargetRangeSchedulesByName(
        string profileName,
        CancellationToken ct = default
    )
    {
        var data = await _targetRangeRepo.GetByProfileNameAsync(profileName, ct);
        return Ok(data);
    }

    /// <summary>
    /// Get a target range schedule by ID
    /// </summary>
    [HttpGet("target-range/by-id/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TargetRangeSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TargetRangeSchedule>> GetTargetRangeScheduleById(
        Guid id,
        CancellationToken ct = default
    )
    {
        var result = await _targetRangeRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new target range schedule
    /// </summary>
    [HttpPost("target-range")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetTargetRangeSchedulesByName"])]
    [ProducesResponseType(typeof(TargetRangeSchedule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TargetRangeSchedule>> CreateTargetRangeSchedule(
        [FromBody] TargetRangeSchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        var created = await _targetRangeRepo.CreateAsync(model, ct);
        return CreatedAtAction(
            nameof(GetTargetRangeScheduleById),
            new { id = created.Id },
            created
        );
    }

    /// <summary>
    /// Update an existing target range schedule
    /// </summary>
    [HttpPut("target-range/{id:guid}")]
    [RemoteCommand(
        Invalidates = [
            "GetProfileSummary",
            "GetTargetRangeSchedulesByName",
            "GetTargetRangeScheduleById",
        ]
    )]
    [ProducesResponseType(typeof(TargetRangeSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TargetRangeSchedule>> UpdateTargetRangeSchedule(
        Guid id,
        [FromBody] TargetRangeSchedule model,
        CancellationToken ct = default
    )
    {
        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");
        try
        {
            var updated = await _targetRangeRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a target range schedule
    /// </summary>
    [HttpDelete("target-range/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetProfileSummary", "GetTargetRangeSchedulesByName"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteTargetRangeSchedule(
        Guid id,
        CancellationToken ct = default
    )
    {
        try
        {
            await _targetRangeRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    /// <summary>
    /// Computes schedule change information for a given date range.
    /// </summary>
    /// <typeparam name="T">Schedule record type implementing <see cref="IV4Record"/>.</typeparam>
    /// <param name="schedules">All schedule records to evaluate.</param>
    /// <param name="from">Start of the date range (inclusive).</param>
    /// <param name="to">End of the date range (inclusive).</param>
    /// <returns>A <see cref="ScheduleChangeInfo"/> describing change activity within the range.</returns>
    private static ScheduleChangeInfo ComputeChangeInfo<T>(
        IEnumerable<T> schedules,
        DateTime from,
        DateTime to
    )
        where T : IV4Record
    {
        var inRange = schedules.Where(s => s.Timestamp >= from && s.Timestamp <= to).ToList();
        return new ScheduleChangeInfo
        {
            ChangedDuringPeriod = inRange.Count > 0,
            LastChangedAt = inRange.OrderByDescending(s => s.Timestamp).FirstOrDefault() is { } last
                ? last.Timestamp
                : null,
            ChangeCount = inRange.Count,
        };
    }
}

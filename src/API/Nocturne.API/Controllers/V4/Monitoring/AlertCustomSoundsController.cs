using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for managing custom alert sounds (upload, list, stream, delete).
/// </summary>
/// <seealso cref="NocturneDbContext"/>
[ApiController]
[Tags("Monitoring")]
[Authorize]
[Route("api/v4/alert-sounds")]
public class AlertCustomSoundsController : ControllerBase
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;
    private readonly ILogger<AlertCustomSoundsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertCustomSoundsController"/>.
    /// </summary>
    /// <param name="contextFactory">Factory for creating <see cref="NocturneDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    public AlertCustomSoundsController(
        IDbContextFactory<NocturneDbContext> contextFactory,
        ILogger<AlertCustomSoundsController> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Upload a custom alert sound file.
    /// </summary>
    [HttpPost]
    [RemoteCommand]
    [RequestSizeLimit(512_000)]
    [ProducesResponseType(typeof(AlertCustomSoundResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertCustomSoundResponse>> UploadSound(
        [FromForm] IFormFile file, CancellationToken ct)
    {
        if (file.Length > 512_000)
            return Problem(detail: "File exceeds 500KB limit", statusCode: 400, title: "Bad Request");

        if (!file.ContentType.StartsWith("audio/"))
            return Problem(detail: "File must be an audio type", statusCode: 400, title: "Bad Request");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entity = new AlertCustomSoundEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            Name = file.FileName,
            MimeType = file.ContentType,
            Data = ms.ToArray(),
            FileSize = (int)file.Length,
            CreatedAt = DateTime.UtcNow,
        };

        db.AlertCustomSounds.Add(entity);
        await db.SaveChangesAsync(ct);

        var response = new AlertCustomSoundResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            MimeType = entity.MimeType,
            FileSize = entity.FileSize,
            CreatedAt = entity.CreatedAt,
        };

        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// List all custom sounds for the current tenant (metadata only, no audio data).
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<AlertCustomSoundResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AlertCustomSoundResponse>>> GetSounds(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var sounds = await db.AlertCustomSounds
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new AlertCustomSoundResponse
            {
                Id = s.Id,
                Name = s.Name,
                MimeType = s.MimeType,
                FileSize = s.FileSize,
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(sounds);
    }

    /// <summary>
    /// Stream the raw audio bytes for a custom sound.
    /// </summary>
    [HttpGet("{id:guid}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> StreamSound(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await db.AlertCustomSounds
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (entity is null)
            return NotFound();

        Response.Headers.CacheControl = "max-age=86400";
        return File(entity.Data, entity.MimeType);
    }

    /// <summary>
    /// Delete a custom sound.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetSounds"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSound(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await db.AlertCustomSounds
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (entity is null)
            return NotFound();

        db.AlertCustomSounds.Remove(entity);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }
}

#region DTOs

public class AlertCustomSoundResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public int FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
}

#endregion

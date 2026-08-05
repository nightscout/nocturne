using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Controllers.V4.Admin;

/// <summary>
/// Internal admin controller for demo tenant lifecycle management.
/// </summary>
/// <remarks>
/// Called by the demo background service, which authenticates with the instance key — that
/// yields the <c>platform_admin</c> role, so no caller change was needed to gate this. It was
/// previously <c>[AllowAnonymous]</c> on the assumption it was unreachable from outside; it is
/// in fact tenantless-allowed and reachable through the web app's <c>/api</c> proxy, which made
/// demo-tenant provisioning and deletion anonymous operations.
/// </remarks>
[ApiController]
[Route("api/v4/admin/demo")]
[Authorize(Roles = "platform_admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class DemoAdminController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly IDbContextFactory<NocturneDbContext> _factory;

    public DemoAdminController(ITenantService tenantService, IDbContextFactory<NocturneDbContext> factory)
    {
        _tenantService = tenantService;
        _factory = factory;
    }

    /// <summary>
    /// Idempotent provisioning: creates the demo tenant if it doesn't exist, otherwise returns current state.
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(typeof(DemoStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Provision(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var existing = await db.Set<TenantEntity>()
            .Include(t => t.DemoConfig)
            .FirstOrDefaultAsync(t => t.IsDemo, ct);

        if (existing is not null)
            return Ok(ToDto(existing, alreadyExisted: true));

        var created = await _tenantService.CreateWithoutOwnerAsync("demo", "Nocturne Demo", ct);

        var tenant = await db.Set<TenantEntity>()
            .FirstAsync(t => t.Id == created.Id, ct);

        tenant.IsDemo = true;

        var config = new TenantDemoConfigEntity { TenantId = tenant.Id };
        db.Set<TenantDemoConfigEntity>().Add(config);

        await db.SaveChangesAsync(ct);

        // Grant the Public subject write access so the demo service can write
        // entries/treatments without auth and visitors can use the API playground.
        await GrantPublicWriteAccessAsync(db, created.Id, ct);

        tenant.DemoConfig = config;
        return Ok(ToDto(tenant, alreadyExisted: false));
    }

    /// <summary>
    /// Update demo tenant operational state.
    /// </summary>
    [HttpPatch("status")]
    [ProducesResponseType(typeof(DemoStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus([FromBody] DemoStatusPatchDto patch, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>()
            .Include(t => t.DemoConfig)
            .FirstOrDefaultAsync(t => t.IsDemo, ct);

        if (tenant is null)
            return NotFound();

        var config = tenant.DemoConfig;
        if (config is null)
            return NotFound();

        if (patch.NextResetAt.HasValue)
            config.NextResetAt = patch.NextResetAt.Value;

        if (patch.LastResetAt.HasValue)
            config.LastResetAt = patch.LastResetAt.Value;

        if (patch.IsActive.HasValue)
            tenant.IsActive = patch.IsActive.Value;

        await db.SaveChangesAsync(ct);

        return Ok(ToDto(tenant, alreadyExisted: true));
    }

    /// <summary>
    /// Get demo tenant current state.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(DemoStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>()
            .Include(t => t.DemoConfig)
            .FirstOrDefaultAsync(t => t.IsDemo, ct);

        if (tenant is null)
            return NotFound();

        return Ok(ToDto(tenant, alreadyExisted: true));
    }

    /// <summary>
    /// Deletes all demo entries (sensor glucose, meter glucose, calibrations) for the demo tenant.
    /// </summary>
    [HttpDelete("entries")]
    [ProducesResponseType(typeof(DemoDeleteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEntries(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.IsDemo, ct);
        if (tenant is null)
            return NotFound();

        db.TenantId = tenant.Id;

        var deleted = 0L;
        deleted += await db.SensorGlucose.Where(e => e.DataSource == DataSources.DemoService || (e.DataSource == null && e.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.MeterGlucose.Where(e => e.DataSource == DataSources.DemoService || (e.DataSource == null && e.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.Calibrations.Where(e => e.DataSource == DataSources.DemoService || (e.DataSource == null && e.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);

        return Ok(new DemoDeleteResultDto(deleted));
    }

    /// <summary>
    /// Deletes all demo treatments (boluses, carbs, BG checks, notes, device events, bolus calculations, temp basals, state spans, APS snapshots) for the demo tenant.
    /// </summary>
    [HttpDelete("treatments")]
    [ProducesResponseType(typeof(DemoDeleteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTreatments(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.IsDemo, ct);
        if (tenant is null)
            return NotFound();

        db.TenantId = tenant.Id;

        var deleted = 0L;
        deleted += await db.Boluses.Where(b => b.DataSource == DataSources.DemoService || (b.DataSource == null && b.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.CarbIntakes.Where(c => c.DataSource == DataSources.DemoService || (c.DataSource == null && c.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.BGChecks.Where(b => b.DataSource == DataSources.DemoService || (b.DataSource == null && b.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.Notes.Where(n => n.DataSource == DataSources.DemoService || (n.DataSource == null && n.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.DeviceEvents.Where(de => de.DataSource == DataSources.DemoService || (de.DataSource == null && de.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.BolusCalculations.Where(bc => bc.DataSource == DataSources.DemoService || (bc.DataSource == null && bc.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.TempBasals.Where(t => t.DataSource == DataSources.DemoService || (t.DataSource == null && t.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.StateSpans.Where(s => s.Source == DataSources.DemoService).ExecuteDeleteAsync(ct);
        deleted += await db.ApsSnapshots.Where(a => a.Device == DataSources.DemoService).ExecuteDeleteAsync(ct);

        return Ok(new DemoDeleteResultDto(deleted));
    }

    /// <summary>
    /// Deletes all demo data (entries + treatments) for the demo tenant.
    /// </summary>
    [HttpDelete("data")]
    [ProducesResponseType(typeof(DemoDeleteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAllData(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.IsDemo, ct);
        if (tenant is null)
            return NotFound();

        db.TenantId = tenant.Id;

        var deleted = 0L;

        // Entries
        deleted += await db.SensorGlucose.Where(e => e.DataSource == DataSources.DemoService || (e.DataSource == null && e.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.MeterGlucose.Where(e => e.DataSource == DataSources.DemoService || (e.DataSource == null && e.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.Calibrations.Where(e => e.DataSource == DataSources.DemoService || (e.DataSource == null && e.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);

        // Treatments
        deleted += await db.Boluses.Where(b => b.DataSource == DataSources.DemoService || (b.DataSource == null && b.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.CarbIntakes.Where(c => c.DataSource == DataSources.DemoService || (c.DataSource == null && c.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.BGChecks.Where(b => b.DataSource == DataSources.DemoService || (b.DataSource == null && b.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.Notes.Where(n => n.DataSource == DataSources.DemoService || (n.DataSource == null && n.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.DeviceEvents.Where(de => de.DataSource == DataSources.DemoService || (de.DataSource == null && de.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.BolusCalculations.Where(bc => bc.DataSource == DataSources.DemoService || (bc.DataSource == null && bc.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.TempBasals.Where(t => t.DataSource == DataSources.DemoService || (t.DataSource == null && t.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.StateSpans.Where(s => s.Source == DataSources.DemoService).ExecuteDeleteAsync(ct);
        deleted += await db.ApsSnapshots.Where(a => a.Device == DataSources.DemoService).ExecuteDeleteAsync(ct);

        // Seed-extras data (sleep stages/samples cascade from their session).
        // Tracker definitions and alert rules are configuration, not data —
        // seed-extras re-upserts them, so only their instances/history go.
        deleted += await db.HeartRates.Where(h => h.DataSource == DataSources.DemoService || (h.DataSource == null && h.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.StepCounts.Where(s => s.DataSource == DataSources.DemoService || (s.DataSource == null && s.Device == DataSources.DemoService)).ExecuteDeleteAsync(ct);
        deleted += await db.SleepSessions.Where(s => s.SourceApp == DataSources.DemoService).ExecuteDeleteAsync(ct);
        deleted += await db.TrackerInstances.ExecuteDeleteAsync(ct);
        deleted += await db.AlertInstances.ExecuteDeleteAsync(ct);
        deleted += await db.AlertExcursions.ExecuteDeleteAsync(ct);

        return Ok(new DemoDeleteResultDto(deleted));
    }

    /// <summary>
    /// Seeds the demo tenant with the non-glucose sample set: device changes,
    /// sleep sessions, heart rate, step counts, consumable trackers, and alert
    /// rules with alarm history. Called by the demo background service after
    /// each regenerate; entries/treatments arrive separately via the streaming
    /// v1 posts. Idempotent — trackers, sleep, activity, and alarm history
    /// upsert or rebuild rather than duplicate. Trackers are owned by the demo
    /// tenant's Public subject, which anonymous visitors browse as.
    /// </summary>
    [HttpPost("seed-extras")]
    [ProducesResponseType(typeof(SampleDataSeedResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SeedExtras(
        [FromBody] DemoSeedExtrasDto? request,
        [FromServices] SampleDataSeeder seeder,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.IsDemo, ct);
        if (tenant is null)
            return NotFound();

        db.TenantId = tenant.Id;
        var publicSubjectId = await db.TenantMembers
            .Where(m => m.TenantId == tenant.Id && m.Subject!.IsSystemSubject && m.Subject.Name == "Public")
            .Select(m => (Guid?)m.SubjectId)
            .FirstOrDefaultAsync(ct);

        var seeded = await seeder.SeedAsync(
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive),
            request?.Days ?? 7,
            publicSubjectId,
            DataSources.DemoService,
            includeGlucose: false,
            ct);

        return Ok(seeded);
    }

    /// <summary>
    /// Ensures a demo PatientInsulin record exists for the demo tenant.
    /// Creates a default rapid-acting insulin (Humalog) if none exists.
    /// </summary>
    [HttpPost("ensure-insulin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EnsureInsulin(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.IsDemo, ct);
        if (tenant is null)
            return NotFound();

        db.TenantId = tenant.Id;

        var exists = await db.PatientInsulins.AnyAsync(ct);
        if (exists)
            return Ok(new { created = false });

        var insulin = new PatientInsulinEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            InsulinCategory = "RapidActing",
            Name = "Humalog",
            IsCurrent = true,
            Dia = 4.0,
            Peak = 75,
            Curve = "rapid-acting",
            Concentration = 100,
            Role = "Bolus",
            IsPrimary = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };

        db.PatientInsulins.Add(insulin);
        await db.SaveChangesAsync(ct);

        return Ok(new { created = true });
    }

    /// <summary>
    /// Assigns the Admin role to the Public system subject's membership on the demo tenant,
    /// granting unauthenticated read/write access to glucose, treatments, and devices.
    /// </summary>
    private static async Task GrantPublicWriteAccessAsync(NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        db.TenantId = tenantId;

        var publicMember = await db.TenantMembers
            .Include(m => m.Subject)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Subject!.IsSystemSubject && m.Subject.Name == "Public", ct);

        if (publicMember is null)
            return;

        var adminRole = await db.TenantRoles
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Slug == TenantPermissions.SeedRoles.Admin, ct);

        if (adminRole is null)
            return;

        var alreadyAssigned = await db.TenantMemberRoles
            .AnyAsync(mr => mr.TenantMemberId == publicMember.Id && mr.TenantRoleId == adminRole.Id, ct);

        if (alreadyAssigned)
            return;

        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = publicMember.Id,
            TenantRoleId = adminRole.Id,
            SysCreatedAt = DateTime.UtcNow,
        });

        // Also remove the 24-hour limit so the full history is visible
        publicMember.LimitTo24Hours = false;

        await db.SaveChangesAsync(ct);
    }

    private static DemoStateDto ToDto(TenantEntity tenant, bool alreadyExisted) => new(
        TenantId: tenant.Id,
        Slug: tenant.Slug,
        IsActive: tenant.IsActive,
        NextResetAt: tenant.DemoConfig?.NextResetAt,
        LastResetAt: tenant.DemoConfig?.LastResetAt,
        AccessMode: tenant.DemoConfig?.AccessMode ?? "open",
        BackfillDays: tenant.DemoConfig?.BackfillDays ?? 90,
        IntervalMinutes: tenant.DemoConfig?.IntervalMinutes ?? 5,
        ResetIntervalMinutes: tenant.DemoConfig?.ResetIntervalMinutes ?? 0,
        AlreadyExisted: alreadyExisted);
}

public record DemoStateDto(
    Guid TenantId,
    string Slug,
    bool IsActive,
    DateTime? NextResetAt,
    DateTime? LastResetAt,
    string AccessMode,
    int BackfillDays,
    int IntervalMinutes,
    int ResetIntervalMinutes,
    bool AlreadyExisted);

public record DemoStatusPatchDto(
    DateTime? NextResetAt = null,
    DateTime? LastResetAt = null,
    bool? IsActive = null);

public record DemoDeleteResultDto(long DeletedCount);

public record DemoSeedExtrasDto(int Days = 7);

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
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
    private readonly DemoTenantService _demoTenantService;
    private readonly IDbContextFactory<NocturneDbContext> _factory;

    public DemoAdminController(
        ITenantService tenantService,
        DemoTenantService demoTenantService,
        IDbContextFactory<NocturneDbContext> factory)
    {
        _tenantService = tenantService;
        _demoTenantService = demoTenantService;
        _factory = factory;
    }

    /// <summary>
    /// Idempotent provisioning: creates the demo tenant if it doesn't exist, otherwise returns current state.
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(typeof(DemoStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Provision(
        [FromServices] IMemoryCache cache,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var existing = await db.Set<TenantEntity>()
            .Include(t => t.DemoConfig)
            .FirstOrDefaultAsync(t => t.IsDemo, ct);

        if (existing is not null)
        {
            // Re-apply the tenant defaults and the access grants: this runs on every demo
            // service start, so it repairs a tenant left without roles or a demo member by a
            // reset that failed between wiping and re-seeding.
            DemoTenantService.ApplyTenantDefaults(existing);
            await db.SaveChangesAsync(ct);
            await _demoTenantService.ConfigureAccessAsync(existing.Id, ct);
            return Ok(ToDto(existing, alreadyExisted: true));
        }

        var created = await _tenantService.CreateWithoutOwnerAsync("demo", "Nocturne Demo", ct);

        var tenant = await db.Set<TenantEntity>()
            .FirstAsync(t => t.Id == created.Id, ct);

        tenant.IsDemo = true;
        DemoTenantService.ApplyTenantDefaults(tenant);

        var config = new TenantDemoConfigEntity { TenantId = tenant.Id };
        db.Set<TenantDemoConfigEntity>().Add(config);

        await db.SaveChangesAsync(ct);

        // TenantResolutionMiddleware caches the resolved context, IsDemo included, and
        // CreateWithoutOwnerAsync may already have populated it. IsDemo gates the demo sign-in
        // endpoint and the status field the login page keys off, so without this the tenant is
        // non-demo for the cache lifetime — a login page with no passkey and no way in.
        TenantResolutionMiddleware.EvictTenant(cache, tenant.Slug);

        // Open the Public subject's share grant and create the demo member visitors are
        // signed in as.
        await _demoTenantService.ConfigureAccessAsync(created.Id, ct);

        tenant.DemoConfig = config;
        return Ok(ToDto(tenant, alreadyExisted: false));
    }

    /// <summary>
    /// Update demo tenant operational state.
    /// </summary>
    [HttpPatch("status")]
    [ProducesResponseType(typeof(DemoStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(
        [FromBody] DemoStatusPatchDto patch,
        [FromServices] IMemoryCache cache,
        CancellationToken ct)
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

        var activeChanged = patch.IsActive.HasValue && patch.IsActive.Value != tenant.IsActive;
        if (patch.IsActive.HasValue)
            tenant.IsActive = patch.IsActive.Value;

        await db.SaveChangesAsync(ct);

        // IsActive is carried on the cached tenant context, same as IsDemo — so without evicting,
        // a deactivated demo keeps being served for the cache lifetime.
        if (activeChanged)
            TenantResolutionMiddleware.EvictTenant(cache, tenant.Slug);

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
    /// Resets the demo tenant to a freshly provisioned state, discarding both the
    /// generated data and every configuration change a visitor made — settings, roles,
    /// members, connectors, alert rules, trackers and audit history. The tenant keeps
    /// its id, slug and share token. Called by the demo background service before each
    /// regenerate.
    /// </summary>
    [HttpPost("reset")]
    [ProducesResponseType(typeof(DemoResetResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        var tenantId = await _demoTenantService.ResetAsync(ct);
        if (tenantId is null)
            return NotFound();

        return Ok(new DemoResetResultDto(tenantId.Value));
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
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo),
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

public record DemoResetResultDto(Guid TenantId);

public record DemoSeedExtrasDto(int Days = 7);

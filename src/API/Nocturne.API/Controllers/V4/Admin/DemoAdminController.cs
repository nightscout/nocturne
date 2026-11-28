using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Demo;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Services.Demo.Services;

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
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly IDemoDataPurge _purge;

    public DemoAdminController(
        ITenantService tenantService,
        DemoTenantService demoTenantService,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        IDemoDataPurge purge)
    {
        _tenantService = tenantService;
        _demoTenantService = demoTenantService;
        _tenants = tenants;
        _members = members;
        _purge = purge;
    }

    /// <summary>
    /// Idempotent provisioning: creates the demo host if it doesn't exist, then provisions its access and the demo clinic
    /// beside it (<see cref="DemoTenantService.ProvisionAsync"/>), and returns the host's state.
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(typeof(DemoStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Provision(
        [FromServices] IMemoryCache cache,
        CancellationToken ct)
    {
        var existing = await _tenants.FindDemoTenantAsync(ct);

        if (existing is not null)
        {
            // Runs on every demo service start, so it repairs a demo left without roles, members or its
            // clinic by a reset that failed between wiping and re-seeding.
            await _demoTenantService.ProvisionAsync(existing.Id, ct);
            TenantCacheEviction.Evict(cache, existing.Slug, existing.ShareTokenHash);
            return Ok(ToDto((await _tenants.GetAsync(existing.Id, ct))!, alreadyExisted: true));
        }

        var created = await _tenantService.CreateWithoutOwnerAsync("demo", "Nocturne Demo", ct);

        // The demo flag and its config row are one tenant edit, so they land together.
        var createdTenant = (await _tenants.GetAsync(created.Id, ct))!;
        await _tenants.UpdateAsync(
            created.Id,
            _demoTenantService.TenantDefaults(createdTenant) with
            {
                IsDemo = true,
                DemoConfig = new DemoConfigEdit(Kind: DemoTenantKind.Host),
            },
            ct);

        // Open the Public subject's share grant, create the demo member visitors are signed in
        // as, and provision the clinic.
        await _demoTenantService.ProvisionAsync(created.Id, ct);

        // TenantResolutionMiddleware caches the resolved context, IsDemo included, and
        // CreateWithoutOwnerAsync may already have populated it. IsDemo gates the demo sign-in
        // endpoint and the status field the login page keys off, so without this the tenant is
        // non-demo for the cache lifetime: a login page with no passkey and no way in.
        TenantCacheEviction.Evict(cache, created.Slug);

        return Ok(ToDto((await _tenants.GetAsync(created.Id, ct))!, alreadyExisted: false));
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
        var tenant = await _tenants.FindDemoTenantAsync(ct);

        if (tenant is null || tenant.DemoConfig is null)
            return NotFound();

        var activeChanged = patch.IsActive.HasValue && patch.IsActive.Value != tenant.IsActive;

        await _tenants.UpdateAsync(
            tenant.Id,
            new TenantEdit(
                IsActive: patch.IsActive,
                DemoConfig: new DemoConfigEdit(patch.NextResetAt, patch.LastResetAt, NextVisitorResetAt: patch.NextVisitorResetAt)),
            ct);

        tenant = (await _tenants.GetAsync(tenant.Id, ct))!;

        // IsActive is carried on the cached tenant context, same as IsDemo — so without evicting,
        // a deactivated demo keeps being served for the cache lifetime.
        if (activeChanged)
            TenantCacheEviction.Evict(cache, tenant.Slug, tenant.ShareTokenHash);

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
        var tenant = await _tenants.FindDemoTenantAsync(ct);

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
        var tenant = await FindDemoTenantAsync(ct);
        if (tenant is null)
            return NotFound();

        var deleted = await _purge.PurgeEntriesAsync(TenantContextOf(tenant), ct);
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
        var tenant = await FindDemoTenantAsync(ct);
        if (tenant is null)
            return NotFound();

        var deleted = await _purge.PurgeTreatmentsAsync(TenantContextOf(tenant), ct);
        return Ok(new DemoDeleteResultDto(deleted));
    }

    /// <summary>
    /// Resets the whole demo to a freshly provisioned state (<see cref="DemoTenantService.ResetAsync"/>). Called by the
    /// demo background service before each regenerate.
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
    /// Returns the clinic persona's saved views and flag rules to their seeds
    /// (<see cref="DemoTenantService.ResetVisitorStateAsync"/>). Called by the demo background service on its visitor
    /// reset schedule.
    /// </summary>
    [HttpPost("visitor-reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetVisitorState(CancellationToken ct) =>
        await _demoTenantService.ResetVisitorStateAsync(ct) ? NoContent() : NotFound();

    /// <summary>
    /// Seeds the demo tenant with the full sample set — glucose, treatments,
    /// device status, therapy profile, device changes, sleep, activity,
    /// trackers, alert rules with alarm history and notifications, foods,
    /// state spans, patient record, body weight, timezone timeline, clock
    /// face, and a DND example. Called by the demo background service after
    /// each reset. Idempotent — every type upserts or rebuilds rather than
    /// duplicates, except entries/treatments which append (the demo resets
    /// before seeding). Per-user types are owned by the demo tenant's Public
    /// subject, which anonymous visitors browse as. With <see cref="DemoSeedExtrasDto.Person"/>,
    /// seeds that person of the demo clinic from their archetype instead, once: a person who
    /// already holds a sample timeline is left as the live feed has carried it on.
    /// </summary>
    [HttpPost("seed-extras")]
    [ProducesResponseType(typeof(SampleDataSeedResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SeedExtras(
        [FromBody] DemoSeedExtrasDto? request,
        CancellationToken ct)
    {
        var tenant = await FindDemoTenantAsync(ct);
        if (tenant is null)
            return NotFound();

        DemoArchetype? archetype = null;
        if (request?.Person is { } key)
        {
            var person = DemoClinic.People.FirstOrDefault(p => p.Key == key);
            archetype = person is null ? null : DemoArchetypes.Find(person.Key);
            if (person is null || archetype is null)
                return NotFound();

            tenant = await _tenants.FindBySlugAsync(DemoClinic.SlugOf(tenant.Slug, person), ct);
            if (tenant is not { IsDemo: true, DemoConfig.Kind: DemoTenantKind.Person })
                return NotFound();
        }

        var publicSubjectId = (await _members.FindPublicMemberAsync(tenant.Id, ct))?.SubjectId;

        var seeded = await SampleDataSeeder.SeedInTenantScopeAsync(
            HttpContext.RequestServices,
            TenantContextOf(tenant),
            request?.Days ?? 7,
            publicSubjectId,
            DataSources.DemoService,
            includeGlucose: request?.IncludeGlucose ?? true,
            archetype: archetype,
            ct: ct);

        return Ok(seeded);
    }

    private Task<TenantRecord?> FindDemoTenantAsync(CancellationToken ct) => _tenants.FindDemoTenantAsync(ct);

    private static TenantContext TenantContextOf(TenantRecord tenant) =>
        new(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo);

    private static DemoStateDto ToDto(TenantRecord tenant, bool alreadyExisted) => new(
        TenantId: tenant.Id,
        Slug: tenant.Slug,
        IsActive: tenant.IsActive,
        NextResetAt: tenant.DemoConfig?.NextResetAt,
        LastResetAt: tenant.DemoConfig?.LastResetAt,
        AccessMode: tenant.DemoConfig?.AccessMode ?? "open",
        BackfillDays: tenant.DemoConfig?.BackfillDays ?? 90,
        IntervalMinutes: tenant.DemoConfig?.IntervalMinutes ?? 5,
        ResetIntervalMinutes: tenant.DemoConfig?.ResetIntervalMinutes ?? 0,
        NextVisitorResetAt: tenant.DemoConfig?.NextVisitorResetAt,
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
    DateTime? NextVisitorResetAt,
    bool AlreadyExisted);

public record DemoDeleteResultDto(long DeletedCount);

public record DemoResetResultDto(Guid TenantId);

/// <param name="Days">The days of history to seed.</param>
/// <param name="IncludeGlucose">Whether to generate glucose, treatments and device status, or to take them as stored.</param>
/// <param name="Person">The <see cref="DemoClinicPerson.Key"/> of the demo clinic person to seed; null for the demo host.</param>
public record DemoSeedExtrasDto(int Days = 7, bool IncludeGlucose = true, string? Person = null);

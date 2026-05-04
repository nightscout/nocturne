using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Authorization;
using Nocturne.API.Models.DevOnly;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.V4.DevOnly;

/// <summary>
/// Dev-only admin controller for snapshot export/import and connector sync.
/// Conditionally excluded from production builds.
/// </summary>
/// <seealso cref="IConnectorSyncService"/>
/// <seealso cref="ITenantService"/>
[ApiController]
[Route("api/v4/dev-only/admin")]
[AllowAnonymous]
[AllowDuringSetup]
[Produces("application/json")]
public class DevAdminController : ControllerBase
{
    private readonly NocturneDbContext _db;
    private readonly ISecretEncryptionService _encryption;
    private readonly IConnectorSyncService _syncService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITenantService _tenantService;
    private readonly ILogger<DevAdminController> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Initializes a new instance of <see cref="DevAdminController"/>.
    /// </summary>
    /// <param name="db">Database context for direct data access.</param>
    /// <param name="encryption">Service for secret encryption/decryption in snapshots.</param>
    /// <param name="syncService">Service for triggering connector synchronisation.</param>
    /// <param name="tenantAccessor">Accessor for the current request tenant context.</param>
    /// <param name="tenantService">Service for tenant lifecycle management.</param>
    /// <param name="logger">Logger instance.</param>
    public DevAdminController(
        NocturneDbContext db,
        ISecretEncryptionService encryption,
        IConnectorSyncService syncService,
        ITenantAccessor tenantAccessor,
        ITenantService tenantService,
        ILogger<DevAdminController> logger
    )
    {
        _db = db;
        _encryption = encryption;
        _syncService = syncService;
        _tenantAccessor = tenantAccessor;
        _tenantService = tenantService;
        _logger = logger;
    }

    // ── Export ───────────────────────────────────────────────────────────

    /// <summary>
    /// Export a full snapshot of all tenants and their identity/config data.
    /// Secrets are decrypted to plaintext for portability.
    /// </summary>
    [HttpGet("snapshot")]
    public async Task<ActionResult<DevSnapshotDto>> ExportSnapshot(CancellationToken ct)
    {
        _logger.LogInformation("Dev snapshot export started");

        var tenants = await _db.Tenants.AsNoTracking().ToListAsync(ct);
        var tenantSnapshots = new List<TenantSnapshotDto>();

        foreach (var tenant in tenants)
        {
            // Set RLS GUC for tenant-scoped queries
            await SetTenantGuc(tenant.Id, ct);

            // Query tenant-scoped entities
            var roles = await _db.TenantRoles
                .AsNoTracking()
                .Where(r => r.TenantId == tenant.Id)
                .ToListAsync(ct);

            var members = await _db.TenantMembers
                .AsNoTracking()
                .Where(m => m.TenantId == tenant.Id)
                .ToListAsync(ct);

            var memberIds = members.Select(m => m.Id).ToList();
            var memberRoles = await _db.TenantMemberRoles
                .AsNoTracking()
                .Where(mr => memberIds.Contains(mr.TenantMemberId))
                .ToListAsync(ct);

            var oauthClients = await _db.OAuthClients
                .AsNoTracking()
                .Where(c => c.TenantId == tenant.Id)
                .ToListAsync(ct);

            var connectorConfigs = await _db.ConnectorConfigurations
                .AsNoTracking()
                .Where(c => c.TenantId == tenant.Id)
                .ToListAsync(ct);

            // Collect subject IDs from members and query non-scoped entities
            var subjectIds = members.Select(m => m.SubjectId).Distinct().ToList();

            var subjects = await _db.Subjects
                .AsNoTracking()
                .Where(s => subjectIds.Contains(s.Id))
                .ToListAsync(ct);

            var passkeys = await _db.PasskeyCredentials
                .AsNoTracking()
                .Where(p => subjectIds.Contains(p.SubjectId))
                .ToListAsync(ct);

            tenantSnapshots.Add(new TenantSnapshotDto
            {
                Tenant = new TenantEntityDto
                {
                    Id = tenant.Id,
                    Slug = tenant.Slug,
                    DisplayName = tenant.DisplayName,
                    IsActive = tenant.IsActive,
                    LastReadingAt = tenant.LastReadingAt,
                    Timezone = tenant.Timezone,
                    SubjectName = tenant.SubjectName,
                    AllowAccessRequests = tenant.AllowAccessRequests,
                    SysCreatedAt = tenant.SysCreatedAt,
                    SysUpdatedAt = tenant.SysUpdatedAt,
                },
                Subjects = subjects.Select(s => new SubjectEntityDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    Username = s.Username,
                    AccessTokenHash = s.AccessTokenHash,
                    AccessTokenPrefix = s.AccessTokenPrefix,
                    Email = s.Email,
                    Notes = s.Notes,
                    IsActive = s.IsActive,
                    IsSystemSubject = s.IsSystemSubject,
                    CreatedAt = s.CreatedAt,
                    UpdatedAt = s.UpdatedAt,
                    LastLoginAt = s.LastLoginAt,
                    OriginalId = s.OriginalId,
                    PreferredLanguage = s.PreferredLanguage,
                    ApprovalStatus = s.ApprovalStatus,
                    AccessRequestMessage = s.AccessRequestMessage,
                    IsPlatformAdmin = s.IsPlatformAdmin,
                }).ToList(),
                PasskeyCredentials = passkeys.Select(p => new PasskeyCredentialEntityDto
                {
                    Id = p.Id,
                    SubjectId = p.SubjectId,
                    CredentialId = Convert.ToBase64String(p.CredentialId),
                    PublicKey = Convert.ToBase64String(p.PublicKey),
                    SignCount = p.SignCount,
                    Transports = p.Transports,
                    Label = p.Label,
                    CreatedAt = p.CreatedAt,
                    LastUsedAt = p.LastUsedAt,
                    AaGuid = p.AaGuid,
                }).ToList(),
                Roles = roles.Select(r => new TenantRoleEntityDto
                {
                    Id = r.Id,
                    TenantId = r.TenantId,
                    Name = r.Name,
                    Slug = r.Slug,
                    Description = r.Description,
                    Permissions = r.Permissions,
                    IsSystem = r.IsSystem,
                    SysCreatedAt = r.SysCreatedAt,
                    SysUpdatedAt = r.SysUpdatedAt,
                }).ToList(),
                Members = members.Select(m => new TenantMemberEntityDto
                {
                    Id = m.Id,
                    TenantId = m.TenantId,
                    SubjectId = m.SubjectId,
                    SysCreatedAt = m.SysCreatedAt,
                    SysUpdatedAt = m.SysUpdatedAt,
                    DirectPermissions = m.DirectPermissions,
                    Label = m.Label,
                    LimitTo24Hours = m.LimitTo24Hours,
                    CreatedFromInviteId = m.CreatedFromInviteId,
                    LastUsedAt = m.LastUsedAt,
                    LastUsedIp = m.LastUsedIp,
                    LastUsedUserAgent = m.LastUsedUserAgent,
                    RevokedAt = m.RevokedAt,
                }).ToList(),
                MemberRoles = memberRoles.Select(mr => new TenantMemberRoleEntityDto
                {
                    Id = mr.Id,
                    TenantMemberId = mr.TenantMemberId,
                    TenantRoleId = mr.TenantRoleId,
                    SysCreatedAt = mr.SysCreatedAt,
                }).ToList(),
                OAuthClients = oauthClients.Select(c => new OAuthClientEntityDto
                {
                    Id = c.Id,
                    TenantId = c.TenantId,
                    ClientId = c.ClientId,
                    SoftwareId = c.SoftwareId,
                    ClientName = c.ClientName,
                    ClientUri = c.ClientUri,
                    LogoUri = c.LogoUri,
                    CreatedFromIp = c.CreatedFromIp,
                    DisplayName = c.DisplayName,
                    IsKnown = c.IsKnown,
                    RedirectUris = c.RedirectUris,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                }).ToList(),
                ConnectorConfigurations = connectorConfigs.Select(c =>
                {
                    Dictionary<string, string>? plaintext = null;
                    try
                    {
                        var encrypted = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            c.SecretsJson, JsonOptions) ?? [];
                        if (encrypted.Count > 0 && _encryption.IsConfigured)
                            plaintext = _encryption.DecryptSecrets(encrypted);
                        else if (encrypted.Count > 0)
                            _logger.LogWarning(
                                "Encryption not configured; skipping secret decryption for connector {Id}",
                                c.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to decrypt secrets for connector config {Id}", c.Id);
                    }

                    return new ConnectorConfigSnapshotDto
                    {
                        Id = c.Id,
                        TenantId = c.TenantId,
                        ConnectorName = c.ConnectorName,
                        ConfigurationJson = c.ConfigurationJson,
                        SecretsPlaintext = plaintext,
                        SchemaVersion = c.SchemaVersion,
                        LastModified = c.LastModified,
                        ModifiedBy = c.ModifiedBy,
                        SysCreatedAt = c.SysCreatedAt,
                        SysUpdatedAt = c.SysUpdatedAt,
                        LastSyncAttempt = c.LastSyncAttempt,
                        LastSuccessfulSync = c.LastSuccessfulSync,
                        LastErrorMessage = c.LastErrorMessage,
                        LastErrorAt = c.LastErrorAt,
                        IsHealthy = c.IsHealthy,
                    };
                }).ToList(),
            });
        }

        var snapshot = new DevSnapshotDto
        {
            ExportedAt = DateTime.UtcNow,
            Tenants = tenantSnapshots,
        };

        _logger.LogInformation("Dev snapshot export completed: {TenantCount} tenants", tenants.Count);
        return Ok(snapshot);
    }

    // ── Import ───────────────────────────────────────────────────────────

    /// <summary>
    /// Import a snapshot, replacing all identity/config data.
    /// Wraps the entire operation in a transaction.
    /// </summary>
    [HttpPost("snapshot")]
    public async Task<ActionResult> ImportSnapshot(
        [FromBody] DevSnapshotDto snapshot,
        CancellationToken ct)
    {
        _logger.LogInformation("Dev snapshot import started ({TenantCount} tenants)",
            snapshot.Tenants.Count);

        var strategy = _db.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // Collect all subject IDs and passkey IDs from the snapshot for non-scoped upsert
                var allSubjectDtos = snapshot.Tenants.SelectMany(t => t.Subjects).ToList();
                var allPasskeyDtos = snapshot.Tenants.SelectMany(t => t.PasskeyCredentials).ToList();
                var allSubjectIds = allSubjectDtos.Select(s => s.Id).Distinct().ToList();
                var allPasskeyIds = allPasskeyDtos.Select(p => p.Id).Distinct().ToList();

                // Phase 1: Per-tenant scoped cleanup (must happen before subject deletion to avoid FK violations)
                foreach (var ts in snapshot.Tenants)
                {
                    var tenantId = ts.Tenant.Id;
                    await SetTenantGuc(tenantId, ct);

                    // Delete in FK-safe order: member-roles -> members -> roles -> OAuth clients -> connector configs
                    var existingMemberRoles = await _db.TenantMemberRoles
                        .Where(mr => _db.TenantMembers
                            .Where(m => m.TenantId == tenantId)
                            .Select(m => m.Id)
                            .Contains(mr.TenantMemberId))
                        .ToListAsync(ct);
                    _db.TenantMemberRoles.RemoveRange(existingMemberRoles);

                    var existingMembers = await _db.TenantMembers
                        .Where(m => m.TenantId == tenantId)
                        .ToListAsync(ct);
                    _db.TenantMembers.RemoveRange(existingMembers);

                    var existingRoles = await _db.TenantRoles
                        .Where(r => r.TenantId == tenantId)
                        .ToListAsync(ct);
                    _db.TenantRoles.RemoveRange(existingRoles);

                    var existingOAuthClients = await _db.OAuthClients
                        .Where(c => c.TenantId == tenantId)
                        .ToListAsync(ct);
                    _db.OAuthClients.RemoveRange(existingOAuthClients);

                    var existingConnectorConfigs = await _db.ConnectorConfigurations
                        .Where(c => c.TenantId == tenantId)
                        .ToListAsync(ct);
                    _db.ConnectorConfigurations.RemoveRange(existingConnectorConfigs);

                    await _db.SaveChangesAsync(ct);
                }

                // Phase 2: Non-scoped cleanup and upsert (passkeys first due to FK to subjects, then subjects)
                var existingPasskeys = await _db.PasskeyCredentials
                    .Where(p => allPasskeyIds.Contains(p.Id))
                    .ToListAsync(ct);
                _db.PasskeyCredentials.RemoveRange(existingPasskeys);

                var existingSubjects = await _db.Subjects
                    .Where(s => allSubjectIds.Contains(s.Id))
                    .ToListAsync(ct);
                _db.Subjects.RemoveRange(existingSubjects);
                await _db.SaveChangesAsync(ct);

                // Phase 3: Upsert tenants (update-or-insert to avoid cascade-deleting clinical data)
                foreach (var ts in snapshot.Tenants)
                {
                    var td = ts.Tenant;
                    var existingTenant = await _db.Tenants.FindAsync([td.Id], ct);

                    if (existingTenant is not null)
                    {
                        // Update scalar properties in-place
                        existingTenant.Slug = td.Slug;
                        existingTenant.DisplayName = td.DisplayName;
                        existingTenant.IsActive = td.IsActive;
                        existingTenant.LastReadingAt = td.LastReadingAt;
                        existingTenant.Timezone = td.Timezone;
                        existingTenant.SubjectName = td.SubjectName;
                        existingTenant.AllowAccessRequests = td.AllowAccessRequests;
                        existingTenant.SysCreatedAt = td.SysCreatedAt;
                        existingTenant.SysUpdatedAt = td.SysUpdatedAt;
                    }
                    else
                    {
                        _db.Tenants.Add(new()
                        {
                            Id = td.Id,
                            Slug = td.Slug,
                            DisplayName = td.DisplayName,
                            IsActive = td.IsActive,
                            LastReadingAt = td.LastReadingAt,
                            Timezone = td.Timezone,
                            SubjectName = td.SubjectName,
                            AllowAccessRequests = td.AllowAccessRequests,
                            SysCreatedAt = td.SysCreatedAt,
                            SysUpdatedAt = td.SysUpdatedAt,
                        });
                    }
                }
                await _db.SaveChangesAsync(ct);

                // Re-add subjects (deduplicated)
                var addedSubjectIds = new HashSet<Guid>();
                foreach (var s in allSubjectDtos)
                {
                    if (!addedSubjectIds.Add(s.Id)) continue;
                    _db.Subjects.Add(new()
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Username = s.Username,
                        AccessTokenHash = s.AccessTokenHash,
                        AccessTokenPrefix = s.AccessTokenPrefix,
                        Email = s.Email,
                        Notes = s.Notes,
                        IsActive = s.IsActive,
                        IsSystemSubject = s.IsSystemSubject,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt,
                        LastLoginAt = s.LastLoginAt,
                        OriginalId = s.OriginalId,
                        PreferredLanguage = s.PreferredLanguage,
                        ApprovalStatus = s.ApprovalStatus,
                        AccessRequestMessage = s.AccessRequestMessage,
                        IsPlatformAdmin = s.IsPlatformAdmin,
                    });
                }
                await _db.SaveChangesAsync(ct);

                // Re-add passkeys (deduplicated)
                var addedPasskeyIds = new HashSet<Guid>();
                foreach (var p in allPasskeyDtos)
                {
                    if (!addedPasskeyIds.Add(p.Id)) continue;
                    _db.PasskeyCredentials.Add(new()
                    {
                        Id = p.Id,
                        SubjectId = p.SubjectId,
                        CredentialId = Convert.FromBase64String(p.CredentialId),
                        PublicKey = Convert.FromBase64String(p.PublicKey),
                        SignCount = p.SignCount,
                        Transports = p.Transports,
                        Label = p.Label,
                        CreatedAt = p.CreatedAt,
                        LastUsedAt = p.LastUsedAt,
                        AaGuid = p.AaGuid,
                    });
                }
                await _db.SaveChangesAsync(ct);

                // Phase 4: Per-tenant scoped inserts
                foreach (var ts in snapshot.Tenants)
                {
                    var tenantId = ts.Tenant.Id;
                    await SetTenantGuc(tenantId, ct);

                    // Insert roles
                    foreach (var r in ts.Roles)
                    {
                        _db.TenantRoles.Add(new()
                        {
                            Id = r.Id,
                            TenantId = r.TenantId,
                            Name = r.Name,
                            Slug = r.Slug,
                            Description = r.Description,
                            Permissions = r.Permissions,
                            IsSystem = r.IsSystem,
                            SysCreatedAt = r.SysCreatedAt,
                            SysUpdatedAt = r.SysUpdatedAt,
                        });
                    }

                    // Insert members
                    foreach (var m in ts.Members)
                    {
                        _db.TenantMembers.Add(new()
                        {
                            Id = m.Id,
                            TenantId = m.TenantId,
                            SubjectId = m.SubjectId,
                            SysCreatedAt = m.SysCreatedAt,
                            SysUpdatedAt = m.SysUpdatedAt,
                            DirectPermissions = m.DirectPermissions,
                            Label = m.Label,
                            LimitTo24Hours = m.LimitTo24Hours,
                            CreatedFromInviteId = m.CreatedFromInviteId,
                            LastUsedAt = m.LastUsedAt,
                            LastUsedIp = m.LastUsedIp,
                            LastUsedUserAgent = m.LastUsedUserAgent,
                            RevokedAt = m.RevokedAt,
                        });
                    }

                    // Insert member roles
                    foreach (var mr in ts.MemberRoles)
                    {
                        _db.TenantMemberRoles.Add(new()
                        {
                            Id = mr.Id,
                            TenantMemberId = mr.TenantMemberId,
                            TenantRoleId = mr.TenantRoleId,
                            SysCreatedAt = mr.SysCreatedAt,
                        });
                    }

                    // Insert OAuth clients
                    foreach (var c in ts.OAuthClients)
                    {
                        _db.OAuthClients.Add(new()
                        {
                            Id = c.Id,
                            TenantId = c.TenantId,
                            ClientId = c.ClientId,
                            SoftwareId = c.SoftwareId,
                            ClientName = c.ClientName,
                            ClientUri = c.ClientUri,
                            LogoUri = c.LogoUri,
                            CreatedFromIp = c.CreatedFromIp,
                            DisplayName = c.DisplayName,
                            IsKnown = c.IsKnown,
                            RedirectUris = c.RedirectUris,
                            CreatedAt = c.CreatedAt,
                            UpdatedAt = c.UpdatedAt,
                        });
                    }

                    // Insert connector configurations (re-encrypt secrets)
                    foreach (var c in ts.ConnectorConfigurations)
                    {
                        var secretsJson = "{}";
                        if (c.SecretsPlaintext is { Count: > 0 })
                        {
                            if (_encryption.IsConfigured)
                            {
                                var encrypted = _encryption.EncryptSecrets(c.SecretsPlaintext);
                                secretsJson = JsonSerializer.Serialize(encrypted, JsonOptions);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Encryption not configured; skipping secret encryption for connector {Name}",
                                    c.ConnectorName);
                            }
                        }

                        _db.ConnectorConfigurations.Add(new()
                        {
                            Id = c.Id,
                            TenantId = c.TenantId,
                            ConnectorName = c.ConnectorName,
                            ConfigurationJson = c.ConfigurationJson,
                            SecretsJson = secretsJson,
                            SchemaVersion = c.SchemaVersion,
                            LastModified = c.LastModified,
                            ModifiedBy = c.ModifiedBy,
                            SysCreatedAt = c.SysCreatedAt,
                            SysUpdatedAt = c.SysUpdatedAt,
                            LastSyncAttempt = c.LastSyncAttempt,
                            LastSuccessfulSync = c.LastSuccessfulSync,
                            LastErrorMessage = c.LastErrorMessage,
                            LastErrorAt = c.LastErrorAt,
                            IsHealthy = c.IsHealthy,
                        });
                    }

                    await _db.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
            });

            _logger.LogInformation("Dev snapshot import completed successfully");
            return Ok(new { success = true, tenantsImported = snapshot.Tenants.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dev snapshot import failed");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    // ── Sync All ─────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a sync for every configured connector across all tenants.
    /// </summary>
    [HttpPost("sync-all")]
    public async Task<ActionResult> SyncAll(CancellationToken ct)
    {
        _logger.LogInformation("Dev sync-all started");

        // Get all connector configurations across all tenants (need to query per-tenant with RLS)
        var tenants = await _db.Tenants.AsNoTracking().ToListAsync(ct);
        var results = new List<object>();

        foreach (var tenant in tenants)
        {
            await SetTenantGuc(tenant.Id, ct);

            var configs = await _db.ConnectorConfigurations
                .AsNoTracking()
                .Where(c => c.TenantId == tenant.Id)
                .ToListAsync(ct);

            foreach (var config in configs)
            {
                // Set tenant context so the sync service operates in the right tenant
                _tenantAccessor.SetTenant(new TenantContext(
                    tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive));

                try
                {
                    var request = new SyncRequest();
                    var result = await _syncService.TriggerSyncAsync(
                        config.ConnectorName, request, ct);

                    results.Add(new
                    {
                        tenantSlug = tenant.Slug,
                        tenantId = tenant.Id,
                        connectorName = config.ConnectorName,
                        connectorConfigId = config.Id,
                        success = result.Success,
                        message = result.Message,
                        itemsSynced = result.ItemsSynced,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Sync failed for connector {ConnectorName} in tenant {TenantSlug}",
                        config.ConnectorName, tenant.Slug);

                    results.Add(new
                    {
                        tenantSlug = tenant.Slug,
                        tenantId = tenant.Id,
                        connectorName = config.ConnectorName,
                        connectorConfigId = config.Id,
                        success = false,
                        message = ex.Message,
                        itemsSynced = 0,
                    });
                }
            }
        }

        _logger.LogInformation("Dev sync-all completed: {Count} connectors synced", results.Count);
        return Ok(new { results });
    }

    // ── Tenant listing ─────────────────────────────────────────────────────

    /// <summary>
    /// List all tenants with record counts and connector health (dev-only).
    /// Used by the Aspire dashboard "List Tenants" command.
    /// </summary>
    [HttpGet("tenants")]
    public async Task<ActionResult<List<DevTenantSummaryDto>>> ListTenants(CancellationToken ct)
    {
        var tenants = await _db.Tenants.AsNoTracking().ToListAsync(ct);
        var summaries = new List<DevTenantSummaryDto>();

        foreach (var tenant in tenants)
        {
            await SetTenantGuc(tenant.Id, ct);

            var entryCount = (long)await _db.SensorGlucose.CountAsync(ct)
                + await _db.MeterGlucose.CountAsync(ct)
                + await _db.Calibrations.CountAsync(ct);
            var treatmentCount = (long)await _db.Boluses.CountAsync(ct)
                + await _db.CarbIntakes.CountAsync(ct)
                + await _db.BGChecks.CountAsync(ct)
                + await _db.Notes.CountAsync(ct)
                + await _db.DeviceEvents.CountAsync(ct)
                + await _db.TempBasals.CountAsync(ct)
                + await _db.BolusCalculations.CountAsync(ct);
            var deviceStatusCount = await _db.ApsSnapshots.LongCountAsync(ct);
            var profileCount = await _db.TherapySettings.CountAsync(ct);
            var memberCount = await _db.TenantMembers
                .Where(m => m.TenantId == tenant.Id && m.RevokedAt == null)
                .CountAsync(ct);

            var connectors = await _db.ConnectorConfigurations
                .Where(c => c.TenantId == tenant.Id)
                .Select(c => new DevConnectorSummaryDto(
                    c.ConnectorName,
                    c.IsHealthy,
                    c.LastSuccessfulSync,
                    c.LastErrorMessage))
                .ToListAsync(ct);

            var latestEntry = await _db.SensorGlucose
                .OrderByDescending(e => e.Timestamp)
                .Select(e => (DateTime?)e.Timestamp)
                .FirstOrDefaultAsync(ct);

            summaries.Add(new DevTenantSummaryDto(
                tenant.Id,
                tenant.Slug,
                tenant.DisplayName,
                tenant.IsActive,
                tenant.Timezone,
                tenant.SysCreatedAt,
                entryCount,
                treatmentCount,
                deviceStatusCount,
                profileCount,
                memberCount,
                latestEntry,
                connectors));
        }

        return Ok(summaries);
    }

    // ── Tenant creation ────────────────────────────────────────────────────

    /// <summary>
    /// Create a new tenant without authentication (dev-only).
    /// Used by the Aspire dashboard "Create Tenant" command.
    /// </summary>
    [HttpPost("tenants")]
    public async Task<ActionResult<TenantCreatedDto>> CreateTenant(
        [FromBody] DevCreateTenantRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Dev tenant creation: slug={Slug}, displayName={DisplayName}",
            request.Slug, request.DisplayName);

        var validation = await _tenantService.ValidateSlugAsync(request.Slug, ct);
        if (!validation.IsValid)
            return BadRequest(new { error = validation.Message });

        var result = await _tenantService.CreateWithoutOwnerAsync(
            request.Slug, request.DisplayName, ct: ct);

        _logger.LogInformation("Dev tenant created: {TenantId} ({Slug})", result.Id, result.Slug);
        return Created($"/api/v4/admin/tenants/{result.Id}", result);
    }

    // ── Tenant deletion / reset ──────────────────────────────────────────────

    /// <summary>
    /// Delete a tenant and all associated data without authentication (dev-only).
    /// </summary>
    [HttpDelete("tenants/{id:guid}")]
    public async Task<ActionResult> DeleteTenant(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null)
            return NotFound(new { error = $"Tenant {id} not found" });

        _logger.LogInformation("Dev tenant deletion: {TenantId}", id);
        await _tenantService.DeleteAsync(id, ct);
        _logger.LogInformation("Dev tenant deleted: {TenantId}", id);
        return NoContent();
    }

    // ── Scoped snapshot import ────────────────────────────────────────────────

    /// <summary>
    /// Import snapshot data for a single tenant, matched by slug in the
    /// provided snapshot. Upserts referenced subjects and passkeys without
    /// affecting other tenants.
    /// </summary>
    [HttpPost("tenants/{id:guid}/import-snapshot")]
    public async Task<ActionResult> ImportScopedSnapshot(
        Guid id,
        [FromBody] TenantSnapshotDto snapshot,
        CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null)
            return NotFound(new { error = $"Tenant {id} not found" });

        _logger.LogInformation(
            "Scoped snapshot import for tenant {Slug} ({TenantId}): {Roles} roles, {Members} members, {OAuthClients} OAuth clients, {Connectors} connectors",
            tenant.Slug, id, snapshot.Roles.Count, snapshot.Members.Count,
            snapshot.OAuthClients.Count, snapshot.ConnectorConfigurations.Count);

        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // Phase 1: Clean existing scoped data for this tenant
            await SetTenantGuc(id, ct);

            var existingMemberRoles = await _db.TenantMemberRoles
                .Where(mr => _db.TenantMembers
                    .Where(m => m.TenantId == id)
                    .Select(m => m.Id)
                    .Contains(mr.TenantMemberId))
                .ToListAsync(ct);
            _db.TenantMemberRoles.RemoveRange(existingMemberRoles);

            var existingMembers = await _db.TenantMembers.Where(m => m.TenantId == id).ToListAsync(ct);
            _db.TenantMembers.RemoveRange(existingMembers);

            var existingRoles = await _db.TenantRoles.Where(r => r.TenantId == id).ToListAsync(ct);
            _db.TenantRoles.RemoveRange(existingRoles);

            var existingOAuthClients = await _db.OAuthClients.Where(c => c.TenantId == id).ToListAsync(ct);
            _db.OAuthClients.RemoveRange(existingOAuthClients);

            var existingConnectorConfigs = await _db.ConnectorConfigurations.Where(c => c.TenantId == id).ToListAsync(ct);
            _db.ConnectorConfigurations.RemoveRange(existingConnectorConfigs);

            await _db.SaveChangesAsync(ct);

            // Phase 2: Upsert subjects and passkeys referenced by this tenant
            var subjectIds = snapshot.Subjects.Select(s => s.Id).Distinct().ToList();
            var passkeyIds = snapshot.PasskeyCredentials.Select(p => p.Id).Distinct().ToList();

            var existingPasskeys = await _db.PasskeyCredentials.Where(p => passkeyIds.Contains(p.Id)).ToListAsync(ct);
            _db.PasskeyCredentials.RemoveRange(existingPasskeys);

            var existingSubjects = await _db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToListAsync(ct);
            _db.Subjects.RemoveRange(existingSubjects);
            await _db.SaveChangesAsync(ct);

            var addedSubjectIds = new HashSet<Guid>();
            foreach (var s in snapshot.Subjects)
            {
                if (!addedSubjectIds.Add(s.Id)) continue;
                _db.Subjects.Add(new()
                {
                    Id = s.Id, Name = s.Name, Username = s.Username,
                    AccessTokenHash = s.AccessTokenHash, AccessTokenPrefix = s.AccessTokenPrefix,
                    Email = s.Email, Notes = s.Notes, IsActive = s.IsActive,
                    IsSystemSubject = s.IsSystemSubject, CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
                    LastLoginAt = s.LastLoginAt, OriginalId = s.OriginalId,
                    PreferredLanguage = s.PreferredLanguage, ApprovalStatus = s.ApprovalStatus,
                    AccessRequestMessage = s.AccessRequestMessage, IsPlatformAdmin = s.IsPlatformAdmin,
                });
            }
            await _db.SaveChangesAsync(ct);

            var addedPasskeyIds = new HashSet<Guid>();
            foreach (var p in snapshot.PasskeyCredentials)
            {
                if (!addedPasskeyIds.Add(p.Id)) continue;
                _db.PasskeyCredentials.Add(new()
                {
                    Id = p.Id, SubjectId = p.SubjectId,
                    CredentialId = Convert.FromBase64String(p.CredentialId),
                    PublicKey = Convert.FromBase64String(p.PublicKey),
                    SignCount = p.SignCount, Transports = p.Transports, Label = p.Label,
                    CreatedAt = p.CreatedAt, LastUsedAt = p.LastUsedAt, AaGuid = p.AaGuid,
                });
            }
            await _db.SaveChangesAsync(ct);

            // Phase 3: Insert scoped data, remapping tenant_id to the actual tenant
            foreach (var r in snapshot.Roles)
            {
                _db.TenantRoles.Add(new()
                {
                    Id = r.Id, TenantId = id, Name = r.Name, Slug = r.Slug,
                    Description = r.Description, Permissions = r.Permissions,
                    IsSystem = r.IsSystem, SysCreatedAt = r.SysCreatedAt, SysUpdatedAt = r.SysUpdatedAt,
                });
            }

            foreach (var m in snapshot.Members)
            {
                _db.TenantMembers.Add(new()
                {
                    Id = m.Id, TenantId = id, SubjectId = m.SubjectId,
                    SysCreatedAt = m.SysCreatedAt, SysUpdatedAt = m.SysUpdatedAt,
                    DirectPermissions = m.DirectPermissions, Label = m.Label,
                    LimitTo24Hours = m.LimitTo24Hours, CreatedFromInviteId = m.CreatedFromInviteId,
                    LastUsedAt = m.LastUsedAt, LastUsedIp = m.LastUsedIp,
                    LastUsedUserAgent = m.LastUsedUserAgent, RevokedAt = m.RevokedAt,
                });
            }

            foreach (var mr in snapshot.MemberRoles)
            {
                _db.TenantMemberRoles.Add(new()
                {
                    Id = mr.Id, TenantMemberId = mr.TenantMemberId,
                    TenantRoleId = mr.TenantRoleId, SysCreatedAt = mr.SysCreatedAt,
                });
            }

            foreach (var c in snapshot.OAuthClients)
            {
                _db.OAuthClients.Add(new()
                {
                    Id = c.Id, TenantId = id, ClientId = c.ClientId,
                    SoftwareId = c.SoftwareId, ClientName = c.ClientName,
                    ClientUri = c.ClientUri, LogoUri = c.LogoUri,
                    CreatedFromIp = c.CreatedFromIp, DisplayName = c.DisplayName,
                    IsKnown = c.IsKnown, RedirectUris = c.RedirectUris,
                    CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt,
                });
            }

            foreach (var c in snapshot.ConnectorConfigurations)
            {
                var secretsJson = "{}";
                if (c.SecretsPlaintext is { Count: > 0 })
                {
                    if (_encryption.IsConfigured)
                    {
                        var encrypted = _encryption.EncryptSecrets(c.SecretsPlaintext);
                        secretsJson = JsonSerializer.Serialize(encrypted, JsonOptions);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Encryption not configured; skipping secret encryption for connector {Name}",
                            c.ConnectorName);
                    }
                }

                _db.ConnectorConfigurations.Add(new()
                {
                    Id = c.Id, TenantId = id, ConnectorName = c.ConnectorName,
                    ConfigurationJson = c.ConfigurationJson, SecretsJson = secretsJson,
                    SchemaVersion = c.SchemaVersion, LastModified = c.LastModified,
                    ModifiedBy = c.ModifiedBy, SysCreatedAt = c.SysCreatedAt, SysUpdatedAt = c.SysUpdatedAt,
                    LastSyncAttempt = c.LastSyncAttempt, LastSuccessfulSync = c.LastSuccessfulSync,
                    LastErrorMessage = c.LastErrorMessage, LastErrorAt = c.LastErrorAt, IsHealthy = c.IsHealthy,
                });
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Scoped snapshot import completed for tenant {Slug}", tenant.Slug);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scoped snapshot import failed for tenant {Slug}", tenant.Slug);
            return StatusCode(500, new { success = false, error = ex.Message });
        }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task SetTenantGuc(Guid tenantId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            [tenantId.ToString()],
            ct);
    }
}

public record DevCreateTenantRequest(string Slug, string DisplayName);

public record DevTenantSummaryDto(
    Guid Id,
    string Slug,
    string DisplayName,
    bool IsActive,
    string Timezone,
    DateTime CreatedAt,
    long Entries,
    long Treatments,
    long DeviceStatuses,
    int Profiles,
    int Members,
    DateTime? LatestEntry,
    List<DevConnectorSummaryDto> Connectors);

public record DevConnectorSummaryDto(
    string Name,
    bool IsHealthy,
    DateTime? LastSuccessfulSync,
    string? LastError);

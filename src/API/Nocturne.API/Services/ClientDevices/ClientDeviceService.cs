using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.ClientDevices;

/// <summary>
/// Persists client device registrations. The tenant is the scope's reader's; the subject is passed in
/// from the authenticated request.
/// </summary>
/// <seealso cref="IClientDeviceService"/>
public class ClientDeviceService : IClientDeviceService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IOAuthGrantRepository _grants;
    private readonly TimeProvider _time;
    private readonly ILogger<ClientDeviceService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ClientDeviceService"/> class.</summary>
    /// <param name="grants">The grants devices registered under, for the app name a device is shown with.</param>
    public ClientDeviceService(
        INocturneDbContextFactory contexts,
        IOAuthGrantRepository grants,
        TimeProvider timeProvider,
        ILogger<ClientDeviceService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ClientDeviceDto> RegisterAsync(
        Guid subjectId,
        RegisterDeviceRequest request,
        IReadOnlySet<string> grantedScopes,
        Guid? grantId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.InstallId))
        {
            throw new ArgumentException("InstallId is required.", nameof(request));
        }

        if (!DeviceKinds.IsValid(request.Kind))
        {
            throw new ArgumentException($"Unknown device kind '{request.Kind}'.", nameof(request));
        }

        var accepted = DeviceCapabilities
            .Accept(request.Kind, request.Capabilities, grantedScopes)
            .ToArray();

        var advertisedCount = request.Capabilities.Distinct().Count();
        if (accepted.Length < advertisedCount)
        {
            _logger.LogDebug(
                "Device {InstallId} ({Kind}) advertised {Advertised} capabilities; {Accepted} accepted after kind/scope filtering.",
                request.InstallId, request.Kind, advertisedCount, accepted.Length);
        }

        // Idempotent upsert on (tenant, install_id). Tenant scoping is applied by the global query
        // filter; the unique index backs the race path below if two registrations collide.
        var existing = await db.ClientDevices
            .FirstOrDefaultAsync(d => d.InstallId == request.InstallId, cancellationToken);

        if (existing is not null)
        {
            return await UpdateExistingAsync(db, existing, subjectId, request, accepted, grantId, cancellationToken);
        }

        var entity = new ClientDeviceEntity { InstallId = request.InstallId };
        Apply(entity, subjectId, request, accepted, grantId);
        db.ClientDevices.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Registered {Kind} device {DeviceId} (install {InstallId}) for subject {SubjectId}.",
                entity.Kind, entity.Id, entity.InstallId, subjectId);
            return ToDto(entity);
        }
        catch (DbUpdateException)
        {
            // A concurrent registration may have won the insert (unique install id); fold this call
            // into an update against the winning row if one now exists, otherwise rethrow.
            db.Entry(entity).State = EntityState.Detached;
            var raced = await db.ClientDevices
                .FirstOrDefaultAsync(d => d.InstallId == request.InstallId, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            _logger.LogWarning(
                "Concurrent registration for install {InstallId}; folding into an update.",
                request.InstallId);
            return await UpdateExistingAsync(db, raced, subjectId, request, accepted, grantId, cancellationToken);
        }
    }

    /// <summary>
    /// Updates an existing device row after confirming it belongs to the caller. An install id is
    /// unique per tenant but not per subject, so this guards against one member re-registering — and
    /// thereby hijacking — another member's device.
    /// </summary>
    /// <exception cref="InvalidOperationException">The install id belongs to a different subject.</exception>
    private async Task<ClientDeviceDto> UpdateExistingAsync(
        NocturneDbContext db,
        ClientDeviceEntity existing,
        Guid subjectId,
        RegisterDeviceRequest request,
        string[] capabilities,
        Guid? grantId,
        CancellationToken cancellationToken)
    {
        if (existing.SubjectId != subjectId)
        {
            throw new InvalidOperationException(
                $"Install id '{request.InstallId}' is already registered to another user.");
        }

        Apply(existing, subjectId, request, capabilities, grantId);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(existing);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientDeviceDto>> GetForSubjectAsync(
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var devices = await db.ClientDevices
            .AsNoTracking()
            .Where(d => d.SubjectId == subjectId)
            .OrderByDescending(d => d.LastSeenAt)
            .ToListAsync(cancellationToken);

        var appNames = await _grants.GetClientDisplayNamesAsync(
            devices.Where(d => d.GrantId is not null).Select(d => d.GrantId!.Value).Distinct().ToList(),
            cancellationToken);

        return devices
            .Select(d => ToDto(d, d.GrantId is { } grantId ? appNames.GetValueOrDefault(grantId) : null))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> GetDeviceCountsByGrantAsync(
        IReadOnlyCollection<Guid> grantIds,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        if (grantIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        return await db.ClientDevices
            .Where(d => d.GrantId != null && grantIds.Contains(d.GrantId.Value))
            .GroupBy(d => d.GrantId!.Value)
            .Select(g => new { GrantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GrantId, x => x.Count, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ClientDeviceDto?> RenameAsync(
        Guid deviceId,
        Guid subjectId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var device = await db.ClientDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null || device.SubjectId != subjectId)
        {
            return null;
        }

        device.Label = label;
        device.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(device);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid deviceId,
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var device = await db.ClientDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null || device.SubjectId != subjectId)
        {
            return false;
        }

        db.ClientDevices.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceActionIntent>> GetActiveIntentsAsync(
        Guid deviceId,
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var device = await db.ClientDevices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

        // Not found, not owned by the caller, or a local-engine device (actuates from synced rules).
        if (device is null || device.SubjectId != subjectId || DeviceKinds.HasLocalEngine(device.Kind))
        {
            return [];
        }

        var deviceCaps = new HashSet<string>(device.Capabilities);

        // EndedAt > now admits test fires: AlertDeliveryService.TestFireAsync gives a
        // device_action test excursion a short future end so it appears here for the window
        // and then self-withdraws. Real excursions always end in the past.
        var now = _time.GetUtcNow().UtcDateTime;
        var excursions = await db.AlertExcursions
            .AsNoTracking()
            .Include(e => e.AlertRule)
                .ThenInclude(r => r!.Channels)
            .Include(e => e.Instances)
            .Where(e => (e.EndedAt == null || e.EndedAt > now)
                && e.AlertRule!.IsEnabled
                && e.AlertRule.Channels.Any(c =>
                    c.ChannelType == ChannelType.DeviceAction && c.Destination == device.Kind))
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(cancellationToken);

        var mutedByOwner = await db.AlertExcursionMutes
            .AsNoTracking()
            .Where(m => m.SubjectId == subjectId)
            .Select(m => m.AlertExcursionId)
            .ToHashSetAsync(cancellationToken);

        var intents = new List<DeviceActionIntent>(excursions.Count);
        foreach (var e in excursions)
        {
            var channel = e.AlertRule!.Channels.FirstOrDefault(c =>
                c.ChannelType == ChannelType.DeviceAction && c.Destination == device.Kind);
            if (channel is null)
            {
                continue;
            }

            var effective = DeviceCapabilities.ParseRequestedCapabilities(channel.Metadata)
                .Where(deviceCaps.Contains)
                .ToList();

            // A mute is the owner's own acknowledgement, so their devices read it as one.
            var acknowledged = e.AcknowledgedAt is not null || mutedByOwner.Contains(e.Id);
            var snoozed = e.Instances.Any(i => i.ResolvedAt == null && AlertSnooze.IsSnoozed(i.SnoozedUntil, now));

            intents.Add(new DeviceActionIntent
            {
                Intent = acknowledged ? "acknowledged" : snoozed ? "snoozed" : "opened",
                ExcursionId = e.Id,
                RuleName = e.AlertRule.Name,
                Severity = e.AlertRule.Severity,
                TargetKind = device.Kind,
                Capabilities = effective,
                Acknowledged = acknowledged,
                StartedAt = e.StartedAt,
            });
        }

        return intents;
    }

    private void Apply(
        ClientDeviceEntity entity,
        Guid subjectId,
        RegisterDeviceRequest request,
        string[] capabilities,
        Guid? grantId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        entity.SubjectId = subjectId;
        entity.Kind = request.Kind;
        entity.Label = request.Label;
        entity.Capabilities = capabilities;
        entity.GrantId = grantId;
        entity.LastSeenAt = now;
        entity.UpdatedAt = now;
    }

    internal static ClientDeviceDto ToDto(ClientDeviceEntity e, string? appName = null) => new()
    {
        Id = e.Id,
        InstallId = e.InstallId,
        Kind = e.Kind,
        Label = e.Label,
        AppName = appName,
        LinkedToApp = e.GrantId is not null,
        Capabilities = [.. e.Capabilities],
        LastSeenAt = e.LastSeenAt,
        CreatedAt = e.CreatedAt,
    };
}

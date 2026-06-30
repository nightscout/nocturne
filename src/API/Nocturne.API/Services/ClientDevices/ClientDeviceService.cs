using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.ClientDevices;

/// <summary>
/// Persists client device registrations via <see cref="NocturneDbContext"/>. Tenant is supplied by
/// the data context (carrier); the subject is passed in from the authenticated request.
/// </summary>
/// <seealso cref="IClientDeviceService"/>
public class ClientDeviceService : IClientDeviceService
{
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<ClientDeviceService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ClientDeviceService"/> class.</summary>
    public ClientDeviceService(NocturneDbContext dbContext, ILogger<ClientDeviceService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ClientDeviceDto> RegisterAsync(
        Guid subjectId,
        RegisterDeviceRequest request,
        IReadOnlySet<string> grantedScopes,
        CancellationToken cancellationToken = default)
    {
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
        var existing = await _dbContext.ClientDevices
            .FirstOrDefaultAsync(d => d.InstallId == request.InstallId, cancellationToken);

        if (existing is not null)
        {
            return await UpdateExistingAsync(existing, subjectId, request, accepted, cancellationToken);
        }

        var entity = new ClientDeviceEntity { InstallId = request.InstallId };
        Apply(entity, subjectId, request, accepted);
        _dbContext.ClientDevices.Add(entity);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Registered {Kind} device {DeviceId} (install {InstallId}) for subject {SubjectId}.",
                entity.Kind, entity.Id, entity.InstallId, subjectId);
            return ToDto(entity);
        }
        catch (DbUpdateException)
        {
            // A concurrent registration may have won the insert (unique install id); fold this call
            // into an update against the winning row if one now exists, otherwise rethrow.
            _dbContext.Entry(entity).State = EntityState.Detached;
            var raced = await _dbContext.ClientDevices
                .FirstOrDefaultAsync(d => d.InstallId == request.InstallId, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            _logger.LogWarning(
                "Concurrent registration for install {InstallId}; folding into an update.",
                request.InstallId);
            return await UpdateExistingAsync(raced, subjectId, request, accepted, cancellationToken);
        }
    }

    /// <summary>
    /// Updates an existing device row after confirming it belongs to the caller. An install id is
    /// unique per tenant but not per subject, so this guards against one member re-registering — and
    /// thereby hijacking — another member's device.
    /// </summary>
    /// <exception cref="InvalidOperationException">The install id belongs to a different subject.</exception>
    private async Task<ClientDeviceDto> UpdateExistingAsync(
        ClientDeviceEntity existing,
        Guid subjectId,
        RegisterDeviceRequest request,
        string[] capabilities,
        CancellationToken cancellationToken)
    {
        if (existing.SubjectId != subjectId)
        {
            throw new InvalidOperationException(
                $"Install id '{request.InstallId}' is already registered to another user.");
        }

        Apply(existing, subjectId, request, capabilities);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(existing);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientDeviceDto>> GetForSubjectAsync(
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        var devices = await _dbContext.ClientDevices
            .Where(d => d.SubjectId == subjectId)
            .OrderByDescending(d => d.LastSeenAt)
            .ToListAsync(cancellationToken);

        return devices.Select(ToDto).ToList();
    }

    private static void Apply(
        ClientDeviceEntity entity,
        Guid subjectId,
        RegisterDeviceRequest request,
        string[] capabilities)
    {
        var now = DateTime.UtcNow;
        entity.SubjectId = subjectId;
        entity.Kind = request.Kind;
        entity.Label = request.Label;
        entity.Capabilities = capabilities;
        entity.LastSeenAt = now;
        entity.UpdatedAt = now;
    }

    internal static ClientDeviceDto ToDto(ClientDeviceEntity e) => new()
    {
        Id = e.Id,
        InstallId = e.InstallId,
        Kind = e.Kind,
        Label = e.Label,
        Capabilities = [.. e.Capabilities],
        LastSeenAt = e.LastSeenAt,
        CreatedAt = e.CreatedAt,
    };
}

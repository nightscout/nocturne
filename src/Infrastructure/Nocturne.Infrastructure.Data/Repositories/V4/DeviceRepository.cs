using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device records in the database.
/// </summary>
public class DeviceRepository : IDeviceRepository
{
    private readonly ITenantDbContextFactory _contextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    public DeviceRepository(ITenantDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Gets a device by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The device, or null if not found.</returns>
    public async Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.Devices.FindAsync([id], ct);
        return entity is null ? null : DeviceMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Finds a device by its category, type, and serial number.
    /// </summary>
    /// <param name="category">The device category.</param>
    /// <param name="type">The device type.</param>
    /// <param name="serial">The device serial number.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching device, or null if not found.</returns>
    public async Task<Device?> FindByCategoryTypeAndSerialAsync(DeviceCategory category, string type, string serial, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var categoryStr = category.ToString();
        var entity = await ctx.Devices
            .FirstOrDefaultAsync(e => e.Category == categoryStr && e.Type == type && e.Serial == serial, ct);
        return entity is null ? null : DeviceMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new device record.
    /// </summary>
    /// <returns>The created device.</returns>
    public async Task<Device> CreateAsync(Device model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = DeviceMapper.ToEntity(model);
        ctx.Devices.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return DeviceMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task WidenSeenWindowAsync(Guid id, DateTime timestamp, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        await ctx.Devices
            .Where(e => e.Id == id
                && (e.LastSeenTimestamp < timestamp || e.FirstSeenTimestamp > timestamp))
            .ExecuteUpdateAsync(s => s
                .SetProperty(
                    e => e.LastSeenTimestamp,
                    e => e.LastSeenTimestamp < timestamp ? timestamp : e.LastSeenTimestamp)
                .SetProperty(
                    e => e.FirstSeenTimestamp,
                    e => e.FirstSeenTimestamp > timestamp ? timestamp : e.FirstSeenTimestamp),
                ct);
    }
}

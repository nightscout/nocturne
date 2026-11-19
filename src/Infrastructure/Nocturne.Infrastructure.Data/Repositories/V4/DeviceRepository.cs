using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing device records in the database.
/// </summary>
public class DeviceRepository : IDeviceRepository
{
    private readonly INocturneDbContextFactory _contextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    public DeviceRepository(INocturneDbContextFactory contextFactory)
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<Device>> FindByIdentitiesAsync(
        IReadOnlyCollection<(DeviceCategory Category, string Type, string Serial)> identities,
        CancellationToken ct = default)
    {
        if (identities.Count == 0)
            return [];

        var keys = identities.Select(i => (Category: i.Category.ToString(), i.Type, i.Serial)).ToHashSet();
        var categories = keys.Select(k => k.Category).Distinct().ToList();
        var types = keys.Select(k => k.Type).Distinct().ToList();
        var serials = keys.Select(k => k.Serial).Distinct().ToList();

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.Devices
            .Where(e => categories.Contains(e.Category) && types.Contains(e.Type) && serials.Contains(e.Serial))
            .ToListAsync(ct);
        return [.. entities.Where(e => keys.Contains((e.Category, e.Type, e.Serial))).Select(DeviceMapper.ToDomainModel)];
    }

    /// <inheritdoc />
    public async Task<Device> CreateAsync(Device model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using (var ctx = await _contextFactory.CreateAsync(ct))
        {
            var entity = DeviceMapper.ToEntity(model);
            ctx.Devices.Add(entity);
            try
            {
                await ctx.SaveChangesAsync(ct);
                return DeviceMapper.ToDomainModel(entity);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // A concurrent first sighting stored the device; it is read back below.
            }
        }

        return (await FindByIdentitiesAsync([(model.Category, model.Type, model.Serial)], ct)).Single();
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

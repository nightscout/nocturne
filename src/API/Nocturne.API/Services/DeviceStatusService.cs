using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for device status operations with WebSocket broadcasting
/// </summary>
public class DeviceStatusService : IDeviceStatusService
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly ISignalRBroadcastService _broadcastService;
    private readonly ICacheService _cacheService;
    private readonly ILogger<DeviceStatusService> _logger;
    private const string CollectionName = "devicestatus";

    public DeviceStatusService(
        IPostgreSqlService postgreSqlService,
        ISignalRBroadcastService broadcastService,
        ICacheService cacheService,
        ILogger<DeviceStatusService> logger
    )
    {
        _postgreSqlService = postgreSqlService;
        _broadcastService = broadcastService;
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<DeviceStatus>> GetDeviceStatusAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    )
    {
        // Cache device status only for the common case of recent entries with default pagination
        if (string.IsNullOrEmpty(find) && (count ?? 10) == 10 && (skip ?? 0) == 0)
        {
            const string cacheKey = "devicestatus:current";
            var cacheTtl = TimeSpan.FromSeconds(60);

            var cachedDeviceStatus = await _cacheService.GetAsync<IEnumerable<DeviceStatus>>(
                cacheKey,
                cancellationToken
            );
            if (cachedDeviceStatus != null)
            {
                _logger.LogDebug("Cache HIT for current device status");
                return cachedDeviceStatus;
            }

            _logger.LogDebug("Cache MISS for current device status, fetching from database");
            var deviceStatus = await _postgreSqlService.GetDeviceStatusAsync(
                10,
                0,
                cancellationToken
            );

            if (deviceStatus != null)
            {
                await _cacheService.SetAsync(cacheKey, deviceStatus, cacheTtl, cancellationToken);
                _logger.LogDebug(
                    "Cached current device status with {TTL}s TTL",
                    cacheTtl.TotalSeconds
                );
            }

            return deviceStatus ?? new List<DeviceStatus>();
        }

        // For non-default parameters, go directly to database
        _logger.LogDebug(
            "Bypassing cache for device status with custom parameters (find: {Find}, count: {Count}, skip: {Skip})",
            find,
            count,
            skip
        );
        return await _postgreSqlService.GetDeviceStatusWithAdvancedFilterAsync(
            count: count ?? 10,
            skip: skip ?? 0,
            findQuery: find,
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<DeviceStatus?> GetDeviceStatusByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        return await _postgreSqlService.GetDeviceStatusByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<DeviceStatus>> CreateDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatusEntries,
        CancellationToken cancellationToken = default
    )
    {
        var createdDeviceStatus = await _postgreSqlService.CreateDeviceStatusAsync(
            deviceStatusEntries,
            cancellationToken
        );

        // Invalidate current device status cache since new entries were created
        try
        {
            await _cacheService.RemoveAsync("devicestatus:current", cancellationToken);
            _logger.LogDebug("Invalidated current device status cache after creating new entries");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate current device status cache");
        }

        // Broadcast create events for each device status entry (replaces legacy ctx.bus.emit('storage-socket-create'))
        foreach (var deviceStatus in createdDeviceStatus)
        {
            try
            {
                await _broadcastService.BroadcastStorageCreateAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = deviceStatus }
                );
                _logger.LogDebug(
                    "Broadcasted storage create event for device status {DeviceStatusId}",
                    deviceStatus.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage create event for device status {DeviceStatusId}",
                    deviceStatus.Id
                );
            }
        }

        return createdDeviceStatus;
    }

    /// <inheritdoc />
    public async Task<DeviceStatus?> UpdateDeviceStatusAsync(
        string id,
        DeviceStatus deviceStatus,
        CancellationToken cancellationToken = default
    )
    {
        var updatedDeviceStatus = await _postgreSqlService.UpdateDeviceStatusAsync(
            id,
            deviceStatus,
            cancellationToken
        );

        if (updatedDeviceStatus != null)
        {
            // Invalidate current device status cache since an entry was updated
            try
            {
                await _cacheService.RemoveAsync("devicestatus:current", cancellationToken);
                _logger.LogDebug(
                    "Invalidated current device status cache after updating device status {DeviceStatusId}",
                    id
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate current device status cache");
            }

            try
            {
                await _broadcastService.BroadcastStorageUpdateAsync(
                    CollectionName,
                    new { colName = CollectionName, doc = updatedDeviceStatus }
                );
                _logger.LogDebug(
                    "Broadcasted storage update event for device status {DeviceStatusId}",
                    updatedDeviceStatus.Id
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage update event for device status {DeviceStatusId}",
                    updatedDeviceStatus.Id
                );
            }
        }

        return updatedDeviceStatus;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDeviceStatusAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        // Get the device status before deleting for broadcasting
        var deviceStatusToDelete = await _postgreSqlService.GetDeviceStatusByIdAsync(
            id,
            cancellationToken
        );

        var deleted = await _postgreSqlService.DeleteDeviceStatusAsync(id, cancellationToken);

        if (deleted)
        {
            // Invalidate current device status cache since an entry was deleted
            try
            {
                await _cacheService.RemoveAsync("devicestatus:current", cancellationToken);
                _logger.LogDebug(
                    "Invalidated current device status cache after deleting device status {DeviceStatusId}",
                    id
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate current device status cache");
            }

            if (deviceStatusToDelete != null)
            {
                try
                {
                    await _broadcastService.BroadcastStorageDeleteAsync(
                        CollectionName,
                        new { colName = CollectionName, doc = deviceStatusToDelete }
                    );
                    _logger.LogDebug(
                        "Broadcasted storage delete event for device status {DeviceStatusId}",
                        deviceStatusToDelete.Id
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to broadcast storage delete event for device status {DeviceStatusId}",
                        deviceStatusToDelete.Id
                    );
                }
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<long> DeleteDeviceStatusEntriesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        var deletedCount = await _postgreSqlService.BulkDeleteDeviceStatusAsync(
            find ?? "{}",
            cancellationToken
        );

        if (deletedCount > 0)
        {
            // Invalidate current device status cache since entries were deleted
            try
            {
                await _cacheService.RemoveAsync("devicestatus:current", cancellationToken);
                _logger.LogDebug(
                    "Invalidated current device status cache after bulk deleting {Count} entries",
                    deletedCount
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to invalidate current device status cache");
            }

            // Broadcast bulk delete event (replaces legacy ctx.bus.emit('storage-socket-delete'))
            try
            {
                await _broadcastService.BroadcastStorageDeleteAsync(
                    CollectionName,
                    new { colName = CollectionName, deletedCount = deletedCount }
                );
                _logger.LogDebug(
                    "Broadcasted storage delete event for {Count} device status entries",
                    deletedCount
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to broadcast storage delete event for bulk device status deletion"
                );
            }
        }

        return deletedCount;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<DeviceStatus>> GetRecentDeviceStatusAsync(
        int count = 10,
        CancellationToken cancellationToken = default
    )
    {
        return await _postgreSqlService.GetDeviceStatusAsync(count, 0, cancellationToken);
    }
}

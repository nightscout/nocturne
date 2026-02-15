using Microsoft.Extensions.Logging;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeEventsCache(
    MyLifeSessionStore sessionStore,
    MyLifeSyncService syncService,
    ILogger<MyLifeEventsCache> logger)
{
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _cachedAt;
    private Task<IReadOnlyList<MyLifeEvent>>? _currentTask;
    private DateTime? _since;
    private DateTime? _until;

    public async Task<IReadOnlyList<MyLifeEvent>> GetEventsAsync(
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "MyLife GetEventsAsync called: since={Since:yyyy-MM-dd HH:mm}, until={Until:yyyy-MM-dd HH:mm}",
            since, until);

        if (IsCacheValid(since, until))
        {
            logger.LogInformation("MyLife cache valid, returning cached data");
            return await _currentTask!;
        }

        logger.LogInformation(
            "MyLife cache invalid (cached: since={CachedSince}, until={CachedUntil}), fetching fresh data",
            _since?.ToString("yyyy-MM-dd HH:mm") ?? "null",
            _until?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (IsCacheValid(since, until)) return await _currentTask!;

            if (string.IsNullOrWhiteSpace(sessionStore.AuthToken))
            {
                logger.LogWarning("MyLife auth token missing");
                return [];
            }

            if (string.IsNullOrWhiteSpace(sessionStore.ServiceUrl))
            {
                logger.LogWarning("MyLife service url missing");
                return [];
            }

            if (string.IsNullOrWhiteSpace(sessionStore.PatientId))
            {
                logger.LogWarning("MyLife patient id missing");
                return [];
            }

            _since = since;
            _until = until;
            _cachedAt = DateTime.UtcNow;
            _currentTask = syncService.FetchEventsAsync(
                sessionStore.ServiceUrl,
                sessionStore.AuthToken,
                sessionStore.PatientId,
                since,
                until,
                cancellationToken
            );

            return await _currentTask;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _currentTask = null;
        _since = null;
        _until = null;
    }

    private bool IsCacheValid(DateTime since, DateTime until)
    {
        if (_currentTask == null || !_since.HasValue || !_until.HasValue)
            return false;

        // Cache invalid if requesting earlier data
        if (since < _since.Value)
            return false;

        // Cache invalid if requesting later data (e.g., new month)
        if (until > _until.Value)
            return false;

        // Cache expired
        if (DateTime.UtcNow - _cachedAt > CacheExpiration)
            return false;

        return true;
    }
}
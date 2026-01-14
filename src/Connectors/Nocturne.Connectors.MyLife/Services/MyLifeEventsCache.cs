using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeEventsCache(
    MyLifeSessionStore sessionStore,
    MyLifeSyncService syncService,
    ILogger<MyLifeEventsCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Task<IReadOnlyList<MyLifeEvent>>? _currentTask;
    private DateTime? _since;

    public async Task<IReadOnlyList<MyLifeEvent>> GetEventsAsync(
        DateTime since,
        int maxMonths,
        CancellationToken cancellationToken)
    {
        if (_currentTask != null && _since.HasValue && since >= _since.Value) return await _currentTask;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentTask != null && _since.HasValue && since >= _since.Value) return await _currentTask;

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
            _currentTask = syncService.FetchEventsAsync(
                sessionStore.ServiceUrl,
                sessionStore.AuthToken,
                sessionStore.PatientId,
                since,
                maxMonths,
                cancellationToken
            );

            return await _currentTask;
        }
        finally
        {
            _lock.Release();
        }
    }
}
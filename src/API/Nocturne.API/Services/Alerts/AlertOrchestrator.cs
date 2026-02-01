using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

public class AlertOrchestrator(
    IAlertRulesEngine rulesEngine,
    IAlertProcessingService processingService,
    ILogger<AlertOrchestrator> logger)
    : IAlertOrchestrator
{
    private const string DefaultUserId = "00000000-0000-0000-0000-000000000001";

    public async Task EvaluateAndProcessEntriesAsync(
        IEnumerable<Entry> entries,
        string? userId,
        CancellationToken cancellationToken = default
    )
    {
        var resolvedUserId = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId;

        foreach (var entry in entries)
        {
            try
            {
                var alertEvents = await rulesEngine.EvaluateGlucoseData(
                    entry,
                    resolvedUserId,
                    cancellationToken
                );

                foreach (var alertEvent in alertEvents)
                {
                    try
                    {
                        await processingService.ProcessAlertEvent(alertEvent, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Failed to process alert event {AlertType} for user {UserId}",
                            alertEvent.AlertType,
                            resolvedUserId
                        );
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to evaluate alert rules for entry {EntryId} and user {UserId}",
                    entry.Id ?? "unknown",
                    resolvedUserId
                );
            }
        }
    }
}

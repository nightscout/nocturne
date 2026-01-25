using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

public interface IAlertOrchestrator
{
    Task EvaluateAndProcessEntriesAsync(
        IEnumerable<Entry> entries,
        string? userId,
        CancellationToken cancellationToken = default
    );
}

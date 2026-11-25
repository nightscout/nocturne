using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Hands the facts a committed write moved (<see cref="ContextFactWrites.MovedIn"/>) to
/// <see cref="AlertFactEvaluationScheduler"/>, with the write's tenant captured now, while the scope
/// that wrote is still alive. Every origin counts: an import or a restore moves facts as a live write
/// does.
/// </summary>
internal sealed class AlertFactsChangedSubscriber(ITenantAccessor tenant, AlertFactEvaluationScheduler scheduler)
    : IRecordChangeSubscriber
{
    public RecordChangeStage Stage => RecordChangeStage.HandOff;

    public Task OnChangedAsync(RecordsChanged batch, CancellationToken ct)
    {
        if (tenant.Context is { } context)
            scheduler.Moved(context, ContextFactWrites.MovedIn(batch));
        return Task.CompletedTask;
    }
}

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>A connector service whose background runs bound their backfill crawls.</summary>
public interface IBackfillBudgeted
{
    /// <summary>
    ///     How far into a background run its backfill crawls may still take a page; see
    ///     <see cref="Constants.ConnectorRunLimits.BackfillBudgetFor"/>.
    /// </summary>
    TimeSpan BackfillBudget { get; set; }
}

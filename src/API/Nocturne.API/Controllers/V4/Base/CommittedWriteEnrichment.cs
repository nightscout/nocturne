using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Base;

/// <summary>
/// Runs an enrichment of records a write has already committed, and logs a failure of it instead of
/// throwing. The rule is stated on
/// <see cref="V4ReadOnlyControllerBase{TModel, TRepository}.OnBeforeReturnAsync"/>.
/// </summary>
internal static class CommittedWriteEnrichment
{
    private const int LoggedIdCount = 10;

    /// <param name="controller">The controller whose type names the log category and whose request supplies the logger.</param>
    /// <param name="records">The committed records the enrichment fills.</param>
    /// <param name="enrich">The enrichment.</param>
    /// <param name="ct">Cancellation token. A cancellation of the request is not swallowed.</param>
    internal static async Task RunAsync<TModel>(
        ControllerBase controller,
        IReadOnlyCollection<TModel> records,
        Func<Task> enrich,
        CancellationToken ct)
        where TModel : class, IV4Record
    {
        try
        {
            await enrich();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var logger = controller.HttpContext?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(controller.GetType())
                ?? NullLogger.Instance;
            logger.LogWarning(
                ex,
                "Enrichment of {RecordCount} committed {RecordType} records failed; the stored records are returned unenriched. First ids: {RecordIds}",
                records.Count,
                typeof(TModel).Name,
                string.Join(", ", records.Take(LoggedIdCount).Select(r => r.Id)));
        }
    }
}

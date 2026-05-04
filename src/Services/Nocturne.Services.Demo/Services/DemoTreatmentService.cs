using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Service for managing demo treatments in the database.
/// </summary>
public interface IDemoTreatmentService
{
    Task CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        CancellationToken cancellationToken = default
    );
    Task<long> DeleteAllDemoTreatmentsAsync(CancellationToken cancellationToken = default);
}

public class DemoTreatmentService : IDemoTreatmentService
{
    private readonly ITreatmentDecomposer _decomposer;
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<DemoTreatmentService> _logger;

    public DemoTreatmentService(
        ITreatmentDecomposer decomposer,
        NocturneDbContext dbContext,
        ILogger<DemoTreatmentService> logger
    )
    {
        _decomposer = decomposer;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        CancellationToken cancellationToken = default
    )
    {
        var treatmentList = treatments.ToList();
        if (!treatmentList.Any())
            return;

        // Ensure all treatments are tagged as demo data
        foreach (var treatment in treatmentList)
        {
            treatment.DataSource = DataSources.DemoService;
        }

        foreach (var treatment in treatmentList)
        {
            await _decomposer.DecomposeAsync(treatment, cancellationToken);
        }

        _logger.LogDebug("Created {Count} demo treatments via decomposer", treatmentList.Count);
    }

    public async Task<long> DeleteAllDemoTreatmentsAsync(
        CancellationToken cancellationToken = default
    )
    {
        long count = 0;
        count += await _dbContext.Boluses.Where(b => b.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.CarbIntakes.Where(c => c.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.BGChecks.Where(b => b.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.Notes.Where(n => n.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.DeviceEvents.Where(de => de.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.BolusCalculations.Where(bc => bc.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.TempBasals.Where(t => t.DataSource == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);
        count += await _dbContext.StateSpans.Where(s => s.Source == DataSources.DemoService).ExecuteDeleteAsync(cancellationToken);

        _logger.LogInformation("Deleted {Count} demo treatment records from V4 tables", count);
        return count;
    }
}

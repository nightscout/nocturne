using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Models.CoachMarks;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.CoachMarks;

/// <summary>
/// Persists per-user coach mark progression (unseen, seen, dismissed, completed)
/// and manages timestamp bookkeeping for <see cref="CoachMarkState"/> records.
/// </summary>
/// <seealso cref="ICoachMarkService"/>
public class CoachMarkService : ICoachMarkService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CoachMarkService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CoachMarkService"/>.
    /// </summary>
    /// <param name="dbContext">The database context for coach mark state persistence.</param>
    /// <param name="httpContextAccessor">Accessor for the current HTTP context and authenticated subject.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    public CoachMarkService(
        NocturneDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger<CoachMarkService> logger
    )
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _httpContextAccessor =
            httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Guid SubjectId => _httpContextAccessor.HttpContext!.GetSubjectId()!.Value;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoachMarkState>> GetAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var subjectId = SubjectId;

            _logger.LogDebug("Getting all coach mark states for subject {SubjectId}", subjectId);

            var entities = await _dbContext
                .CoachMarkStates.Where(e => e.SubjectId == subjectId)
                .ToListAsync(cancellationToken);

            return entities.Select(CoachMarkStateMapper.ToDomainModel).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting coach mark states");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CoachMarkState> UpsertAsync(
        string markKey,
        string status,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var subjectId = SubjectId;

            _logger.LogDebug(
                "Upserting coach mark {MarkKey} to status {Status} for subject {SubjectId}",
                markKey,
                status,
                subjectId
            );

            var entity = await _dbContext
                .CoachMarkStates.FirstOrDefaultAsync(
                    e => e.SubjectId == subjectId && e.MarkKey == markKey,
                    cancellationToken
                );

            if (entity is null)
            {
                entity = new Infrastructure.Data.Entities.CoachMarkStateEntity
                {
                    Id = Guid.CreateVersion7(),
                    SubjectId = subjectId,
                    MarkKey = markKey,
                    Status = status,
                };

                _dbContext.CoachMarkStates.Add(entity);
            }
            else
            {
                entity.Status = status;
            }

            // Set SeenAt on first transition to "seen" (or any later state)
            if (entity.SeenAt is null && status is "seen" or "completed" or "dismissed")
            {
                entity.SeenAt = DateTime.UtcNow;
            }

            // Set CompletedAt on first transition to "completed" or "dismissed"
            if (entity.CompletedAt is null && status is "completed" or "dismissed")
            {
                entity.CompletedAt = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            return CoachMarkStateMapper.ToDomainModel(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error upserting coach mark {MarkKey} for subject",
                markKey
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var subjectId = SubjectId;

        _logger.LogDebug("Deleting all coach mark states for subject {SubjectId}", subjectId);

        await _dbContext.CoachMarkStates
            .Where(e => e.SubjectId == subjectId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Sleep;

/// <summary>
/// Thin domain service for <see cref="SleepSession"/> operations,
/// delegating persistence to <see cref="ISleepSessionRepository"/>.
/// </summary>
/// <seealso cref="ISleepService"/>
/// <seealso cref="ISleepSessionRepository"/>
public class SleepService : ISleepService
{
    private readonly ISleepSessionRepository _repository;
    private readonly ILogger<SleepService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SleepService"/>.
    /// </summary>
    /// <param name="repository">The sleep session repository for data access.</param>
    /// <param name="logger">The logger instance.</param>
    public SleepService(
        ISleepSessionRepository repository,
        ILogger<SleepService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SleepSession>> GetSessionsAsync(
        DateTime? from,
        DateTime? to,
        SleepSessionType? type,
        SleepSource? source,
        int limit,
        int offset,
        bool descending,
        bool includeStages,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Getting sleep sessions with type: {Type}, source: {Source}, from: {From}, to: {To}, limit: {Limit}, offset: {Offset}, includeStages: {IncludeStages}",
            type, source, from, to, limit, offset, includeStages);

        return await _repository.GetSessionsAsync(
            from, to, type, source, limit, offset, descending, includeStages, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountSessionsAsync(
        DateTime? from,
        DateTime? to,
        SleepSessionType? type,
        SleepSource? source,
        CancellationToken cancellationToken)
    {
        return await _repository.CountSessionsAsync(
            from, to, type, source, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SleepSession?> GetSessionByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting sleep session by ID: {Id}", id);

        return await _repository.GetSessionByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SleepSession> UpsertSessionAsync(
        SleepSession session,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Upserting sleep session with ID: {Id}, Type: {Type}, Source: {Source}",
            session.Id, session.Type, session.Source);

        return await _repository.UpsertSessionAsync(session, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SleepSession?> UpdateSessionAsync(
        Guid id,
        SleepSession session,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Updating sleep session with ID: {Id}, Type: {Type}",
            id, session.Type);

        return await _repository.UpdateSessionAsync(id, session, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Deleting sleep session with ID: {Id}", id);

        return await _repository.DeleteSessionAsync(id, cancellationToken);
    }
}

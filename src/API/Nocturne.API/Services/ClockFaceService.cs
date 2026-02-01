using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services;

/// <summary>
/// Service for managing clock face configurations
/// </summary>
public class ClockFaceService : IClockFaceService
{
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<ClockFaceService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ClockFaceService(
        NocturneDbContext dbContext,
        ILogger<ClockFaceService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ClockFace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting clock face by ID: {Id}", id);

            var entity = await _dbContext.ClockFaces
                .AsNoTracking()
                .FirstOrDefaultAsync(cf => cf.Id == id, cancellationToken);

            if (entity == null)
            {
                _logger.LogDebug("Clock face not found: {Id}", id);
                return null;
            }

            return MapToModel(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clock face by ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ClockFaceListItem>> GetByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting clock faces for user: {UserId}", userId);

            var entities = await _dbContext.ClockFaces
                .AsNoTracking()
                .Where(cf => cf.UserId == userId)
                .OrderByDescending(cf => cf.UpdatedAt ?? cf.CreatedAt)
                .Select(cf => new ClockFaceListItem
                {
                    Id = cf.Id,
                    Name = cf.Name,
                    CreatedAt = cf.CreatedAt,
                    UpdatedAt = cf.UpdatedAt
                })
                .ToListAsync(cancellationToken);

            _logger.LogDebug("Found {Count} clock faces for user: {UserId}", entities.Count, userId);
            return entities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clock faces for user: {UserId}", userId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ClockFace> CreateAsync(string userId, CreateClockFaceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Creating clock face for user: {UserId}, name: {Name}", userId, request.Name);

            var entity = new ClockFaceEntity
            {
                UserId = userId,
                Name = request.Name,
                ConfigJson = JsonSerializer.Serialize(request.Config, JsonOptions),
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.ClockFaces.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created clock face {Id} for user: {UserId}", entity.Id, userId);
            return MapToModel(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating clock face for user: {UserId}", userId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ClockFace?> UpdateAsync(Guid id, string userId, UpdateClockFaceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Updating clock face {Id} for user: {UserId}", id, userId);

            var entity = await _dbContext.ClockFaces
                .FirstOrDefaultAsync(cf => cf.Id == id && cf.UserId == userId, cancellationToken);

            if (entity == null)
            {
                _logger.LogDebug("Clock face not found or not owned by user: {Id}, {UserId}", id, userId);
                return null;
            }

            if (request.Name != null)
            {
                entity.Name = request.Name;
            }

            if (request.Config != null)
            {
                entity.ConfigJson = JsonSerializer.Serialize(request.Config, JsonOptions);
            }

            entity.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Updated clock face {Id} for user: {UserId}", id, userId);
            return MapToModel(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating clock face {Id} for user: {UserId}", id, userId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Deleting clock face {Id} for user: {UserId}", id, userId);

            var entity = await _dbContext.ClockFaces
                .FirstOrDefaultAsync(cf => cf.Id == id && cf.UserId == userId, cancellationToken);

            if (entity == null)
            {
                _logger.LogDebug("Clock face not found or not owned by user: {Id}, {UserId}", id, userId);
                return false;
            }

            _dbContext.ClockFaces.Remove(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Deleted clock face {Id} for user: {UserId}", id, userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting clock face {Id} for user: {UserId}", id, userId);
            throw;
        }
    }

    private static ClockFace MapToModel(ClockFaceEntity entity)
    {
        var config = string.IsNullOrEmpty(entity.ConfigJson)
            ? new ClockFaceConfig()
            : JsonSerializer.Deserialize<ClockFaceConfig>(entity.ConfigJson, JsonOptions) ?? new ClockFaceConfig();

        return new ClockFace
        {
            Id = entity.Id,
            UserId = entity.UserId,
            Name = entity.Name,
            Config = config,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Manages MyFitnessPal food-matching settings stored as a JSON blob under the key
/// <c>connectors:myfitnesspal:matching-settings</c> in the tenant settings table.
/// </summary>
/// <seealso cref="IMyFitnessPalMatchingSettingsService"/>
public class MyFitnessPalMatchingSettingsService : IMyFitnessPalMatchingSettingsService
{
    private const string SettingsKey = "connectors:myfitnesspal:matching-settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly NocturneDbContext _context;
    private readonly ILogger<MyFitnessPalMatchingSettingsService> _logger;

    public MyFitnessPalMatchingSettingsService(
        NocturneDbContext context,
        ILogger<MyFitnessPalMatchingSettingsService> logger
    )
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MyFitnessPalMatchingSettings?> GetSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == SettingsKey && s.IsActive,
                cancellationToken
            );

            if (entity?.Value != null)
            {
                var settings = JsonSerializer.Deserialize<MyFitnessPalMatchingSettings>(
                    entity.Value,
                    JsonOptions
                );

                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving MyFitnessPal matching settings");
            return null;
        }

        return new MyFitnessPalMatchingSettings();
    }

    public async Task<MyFitnessPalMatchingSettings> SaveSettingsAsync(
        MyFitnessPalMatchingSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var jsonValue = JsonSerializer.Serialize(settings, JsonOptions);
            var now = DateTimeOffset.UtcNow;

            var entity = await _context.Settings.FirstOrDefaultAsync(
                s => s.Key == SettingsKey,
                cancellationToken
            );

            if (entity == null)
            {
                entity = new SettingsEntity
                {
                    Id = Guid.CreateVersion7(),
                    Key = SettingsKey,
                    Value = jsonValue,
                    Mills = now.ToUnixTimeMilliseconds(),
                    SrvCreated = now,
                    SrvModified = now,
                    IsActive = true,
                    Notes = "MyFitnessPal matching settings",
                    App = "nocturne-api",
                };
                _context.Settings.Add(entity);
            }
            else
            {
                entity.Value = jsonValue;
                entity.SrvModified = now;
                entity.Mills = now.ToUnixTimeMilliseconds();
            }

            await _context.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving MyFitnessPal matching settings");
            throw;
        }
    }
}

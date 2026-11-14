using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Seeding;

/// <summary>
/// The simulation a tenant's sample data came from, and how far it is written. Held in the tenant's
/// settings so <see cref="SampleDataLiveFeed"/> can continue it after a restart: the generator only
/// repeats a seed exactly when it starts on the same day, in the same zone, and keeps its loop
/// interruptions clear of the same end.
/// </summary>
/// <param name="FirstDay">The local date of the seed's first step.</param>
/// <param name="SeedEnd">The instant the seed was generated to.</param>
/// <param name="WrittenThrough">Every generated record timestamped before this instant is stored.</param>
/// <param name="ZoneId">The IANA zone the seed laid its days out in.</param>
/// <param name="DataSource">The data source the seed stamped on its records.</param>
internal sealed record SampleDataTimeline(
    DateTime FirstDay,
    DateTime SeedEnd,
    DateTime WrittenThrough,
    string ZoneId,
    string DataSource)
{
    public const string SettingsKey = "nocturne:sample-data-timeline";

    private const string SettingsApp = "nocturne-sample-data";

    public static async Task<SampleDataTimeline?> ReadAsync(NocturneDbContext db, CancellationToken ct)
    {
        var value = await db.Settings.AsNoTracking()
            .Where(s => s.Key == SettingsKey && s.IsActive)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (value is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<SampleDataTimeline>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(
        NocturneDbContext db, SampleDataTimeline timeline, DateTimeOffset now, CancellationToken ct)
    {
        var value = JsonSerializer.Serialize(timeline);
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingsKey, ct);
        if (entity is null)
        {
            db.Settings.Add(new SettingsEntity
            {
                Id = Guid.CreateVersion7(),
                Key = SettingsKey,
                Value = value,
                Mills = now.ToUnixTimeMilliseconds(),
                SrvCreated = now,
                SrvModified = now,
                IsActive = true,
                App = SettingsApp,
            });
        }
        else
        {
            entity.Value = value;
            entity.IsActive = true;
            entity.SrvModified = now;
            entity.Mills = now.ToUnixTimeMilliseconds();
        }

        await db.SaveChangesAsync(ct);
    }
}

using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// The records of every kind the Treatment Log lists, for one date range.
/// </summary>
public sealed record TreatmentLogRecords(
    IReadOnlyList<Bolus> Boluses,
    IReadOnlyList<CarbIntake> CarbIntakes,
    IReadOnlyList<BGCheck> BgChecks,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<DeviceEvent> DeviceEvents,
    IReadOnlyList<BasalInjection> BasalInjections);

/// <summary>
/// The Treatment Log's filter: one record kind (or all) and a free-text search.
/// </summary>
/// <remarks>
/// The search is a case-insensitive substring match over one space-joined string per record: the
/// kind's display name, the kind's descriptive fields, then data source, app and device. The
/// display names are the English ones the log labels each kind with (<c>ENTRY_CATEGORIES</c> in
/// the web app), so typing a kind's label finds its records.
/// </remarks>
public static class TreatmentLogFilter
{
    /// <summary>The records <paramref name="category"/> and <paramref name="search"/> keep.</summary>
    public static TreatmentLogRecords Apply(
        TreatmentLogRecords records, TreatmentLogCategory category, string? search)
    {
        var query = search?.Trim() ?? string.Empty;

        return new TreatmentLogRecords(
            Keep(records.Boluses, category, TreatmentLogCategory.Bolus, query, b =>
                Terms("Insulin", b, b.BolusType?.ToString())),
            Keep(records.CarbIntakes, category, TreatmentLogCategory.Carbs, query, c =>
                Terms("Carbs", c)),
            Keep(records.BgChecks, category, TreatmentLogCategory.BgCheck, query, g =>
                Terms("BG Checks", g, g.GlucoseType?.ToString())),
            Keep(records.Notes, category, TreatmentLogCategory.Note, query, n =>
                Terms("Notes", n, n.Text, n.EventType)),
            Keep(records.DeviceEvents, category, TreatmentLogCategory.DeviceEvent, query, d =>
                Terms("Device Events", d, d.EventType.ToString(), d.Notes)),
            Keep(records.BasalInjections, category, TreatmentLogCategory.BasalInjection, query, i =>
                Terms("Long-acting injection", i, i.InsulinContext?.InsulinName, i.Notes)));
    }

    /// <summary>Records per kind in <paramref name="records"/>.</summary>
    public static TreatmentLogCounts Count(TreatmentLogRecords records)
    {
        var counts = new TreatmentLogCounts
        {
            Bolus = records.Boluses.Count,
            Carbs = records.CarbIntakes.Count,
            BgCheck = records.BgChecks.Count,
            Note = records.Notes.Count,
            DeviceEvent = records.DeviceEvents.Count,
            BasalInjection = records.BasalInjections.Count,
        };
        counts.All = counts.Bolus + counts.Carbs + counts.BgCheck + counts.Note
            + counts.DeviceEvent + counts.BasalInjection;
        return counts;
    }

    private static IReadOnlyList<T> Keep<T>(
        IReadOnlyList<T> records,
        TreatmentLogCategory category,
        TreatmentLogCategory kind,
        string query,
        Func<T, string> searchable)
    {
        if (category != TreatmentLogCategory.All && category != kind)
            return [];
        if (query.Length == 0)
            return records;
        return records
            .Where(r => searchable(r).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Terms(string kindName, V4RecordBase record, params string?[] fields) =>
        string.Join(' ', new[] { kindName }
            .Concat(fields)
            .Append(record.DataSource)
            .Append(record.App)
            .Append(record.Device)
            .Where(t => !string.IsNullOrEmpty(t)));
}

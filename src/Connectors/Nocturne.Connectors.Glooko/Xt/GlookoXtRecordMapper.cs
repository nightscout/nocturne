using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using BolusType = Nocturne.Core.Models.V4.BolusType;
using CalculationType = Nocturne.Core.Models.V4.CalculationType;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     Fans Glooko XT records out into Nocturne records. One XT record can hold a glucose, an
///     insulin dose, carbs, a basal change and a note at once, so each populated facet becomes its
///     own record, keyed <c>glookoxt_{id}_{facet}</c> so a re-read of the same window upserts
///     rather than duplicates.
/// </summary>
/// <remarks>
///     Two populations share the schema and read differently. A patient typing in the app leaves
///     free text in <c>memo</c> and gives a basal a duration. A vendor sync (CamAPS on a YpsoPump
///     is the one seen live) stamps every record with a JSON provenance blob in <c>memo</c>, reports
///     basal as a stream of rate changes with no duration — each in force until the next, zero
///     included — and files bolus-wizard calculations and pump alarms inside that blob. See
///     <see cref="GlookoXtProvenance"/>.
/// </remarks>
public class GlookoXtRecordMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     The note the reference uploader attaches to a super micro bolus. A bolus so marked is
    ///     algorithm-delivered, which the dashboard draws differently from a meal bolus.
    /// </summary>
    private const string SmbMarker = "SMB";

    /// <summary>
    ///     Maps <paramref name="records"/>. <paramref name="productNames"/> turns the catalogue ids a
    ///     record carries into the device name stamped on the result; a record with no known
    ///     product is stamped with its sync source, or failing that with the connector.
    /// </summary>
    public GlookoXtMappedBatch Map(
        IEnumerable<GlookoXtRecord> records,
        string? unitSetting,
        IReadOnlyDictionary<long, string>? productNames = null)
    {
        var list = records as IList<GlookoXtRecord> ?? records.ToList();
        var unit = GlookoXtGlucoseUnits.Resolve(unitSetting, GlucoseValues(list));
        var batch = new GlookoXtMappedBatch();
        var now = DateTime.UtcNow;
        var streamBasals = new List<(long Id, DateTime At, GlookoXtRecord Record, GlookoXtProvenance? Provenance, string Device)>();

        foreach (var record in list)
        {
            if (!TryParseTimestamp(record.RecordedAt, out var timestamp))
            {
                _logger.LogDebug("Skipping Glooko XT record {Id} with unreadable recorded_at {RecordedAt}", record.Id, record.RecordedAt);
                batch.Skipped++;
                continue;
            }

            if (record.Id is null)
            {
                _logger.LogDebug("Skipping Glooko XT record at {Timestamp:O} that carries no id", timestamp);
                batch.Skipped++;
                continue;
            }

            var provenance = GlookoXtProvenance.TryParse(record.Memo);
            var ctx = new Context(record.Id.Value, timestamp, unit, now, DeviceName(record, provenance, productNames), provenance);

            if (IsStreamBasal(record))
                streamBasals.Add((ctx.Id, timestamp, record, provenance, ctx.Device));

            MapOne(record, ctx, batch);
        }

        ChainStreamBasals(streamBasals, batch, now);

        return batch;
    }

    private sealed record Context(long Id, DateTime Timestamp, GlookoXtGlucoseUnits.Unit Unit, DateTime Now, string Device, GlookoXtProvenance? Provenance);

    private static IEnumerable<double> GlucoseValues(IEnumerable<GlookoXtRecord> records)
    {
        foreach (var record in records)
        {
            if (record.GlycemiaCgm is > 0) yield return record.GlycemiaCgm.Value;
            if (record.Glycemia is > 0) yield return record.Glycemia.Value;
        }
    }

    private static string DeviceName(GlookoXtRecord r, GlookoXtProvenance? provenance, IReadOnlyDictionary<long, string>? names)
    {
        if (names is not null)
        {
            if (r.ProductPumpId is { } pump && names.TryGetValue(pump, out var pumpName)) return pumpName;
            if (r.ProductGlycemiaId is { } glyc && names.TryGetValue(glyc, out var glycName)) return glycName;
        }

        return provenance?.Source ?? DataSources.GlookoConnector;
    }

    /// <summary>A basal change reported without a duration: in force until the next one.</summary>
    private static bool IsStreamBasal(GlookoXtRecord r) => r.BasalRate is >= 0 && r.Duration is null or <= 0;

    private void MapOne(GlookoXtRecord r, Context c, GlookoXtMappedBatch batch)
    {
        var placed = false;

        // A CGM product routes the value into glycemia_cgm and echoes it back in glycemia as well,
        // so a record with both is one sensor reading, not a sensor reading plus a fingerstick.
        if (r.GlycemiaCgm is > 0)
        {
            batch.SensorGlucose.Add(new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = c.Timestamp,
                LegacyId = Key(c.Id, "cgm"),
                SyncIdentifier = Key(c.Id, "cgm"),
                Device = c.Device,
                DataSource = DataSources.GlookoConnector,
                Mgdl = GlookoXtGlucoseUnits.ToMgdl(r.GlycemiaCgm.Value, c.Unit),
                Direction = GlucoseDirection.None,
                CreatedAt = c.Now,
                ModifiedAt = c.Now,
            });
            placed = true;
        }
        else if (r.Glycemia is > 0)
        {
            batch.BGChecks.Add(new BGCheck
            {
                Id = Guid.CreateVersion7(),
                Timestamp = c.Timestamp,
                LegacyId = Key(c.Id, "bg"),
                SyncIdentifier = Key(c.Id, "bg"),
                Device = c.Device,
                DataSource = DataSources.GlookoConnector,
                Glucose = GlookoXtGlucoseUnits.ToMgdl(r.Glycemia.Value, c.Unit),
                Units = GlucoseUnit.MgDl,
                GlucoseType = GlucoseType.Finger,
                CreatedAt = c.Now,
                ModifiedAt = c.Now,
            });
            placed = true;
        }

        if (r.FastInsulin is > 0)
        {
            batch.Boluses.Add(NewBolus(c, "fast", r.FastInsulin.Value, r));
            placed = true;
        }

        if (r.CorrectionInsulin is > 0)
        {
            batch.Boluses.Add(NewBolus(c, "corr", r.CorrectionInsulin.Value, r));
            placed = true;
        }

        if (r.SlowInsulin is > 0)
        {
            batch.BasalInjections.Add(new BasalInjection
            {
                Id = Guid.CreateVersion7(),
                Timestamp = c.Timestamp,
                LegacyId = Key(c.Id, "slow"),
                SyncIdentifier = Key(c.Id, "slow"),
                Device = c.Device,
                DataSource = DataSources.GlookoConnector,
                Units = r.SlowInsulin.Value,
                CreatedAt = c.Now,
                ModifiedAt = c.Now,
            });
            placed = true;
        }

        if (r.Carbs is > 0)
        {
            batch.CarbIntakes.Add(new CarbIntake
            {
                Id = Guid.CreateVersion7(),
                Timestamp = c.Timestamp,
                LegacyId = Key(c.Id, "carbs"),
                SyncIdentifier = Key(c.Id, "carbs"),
                Device = c.Device,
                DataSource = DataSources.GlookoConnector,
                Carbs = r.Carbs.Value,
                CreatedAt = c.Now,
                ModifiedAt = c.Now,
            });
            placed = true;
        }

        if (r.BasalRate is >= 0 && r.Duration is > 0)
        {
            batch.TempBasals.Add(NewTempBasal(c, r, c.Timestamp.AddMinutes(r.Duration.Value)));
            placed = true;
        }
        else if (IsStreamBasal(r))
        {
            // Chained once the whole batch is known; see ChainStreamBasals.
            placed = true;
        }
        else if (r.RatePercentage is not null)
        {
            // A percentage alone cannot become units per hour without the schedule it modifies,
            // which Glooko XT does not expose. Dropped rather than guessed.
            _logger.LogDebug("Glooko XT record {Id} carries a basal percentage without a rate; skipped", c.Id);
            placed = true;
        }

        if (r.PumpStop == true)
        {
            batch.DeviceEvents.Add(NewDeviceEvent(c, "suspend", DeviceEventType.PumpSuspend));
            placed = true;
        }

        if (r.PumpResume == true)
        {
            batch.DeviceEvents.Add(NewDeviceEvent(c, "resume", DeviceEventType.PumpResume));
            placed = true;
        }

        if (r.IsPrime == true)
        {
            batch.DeviceEvents.Add(NewDeviceEvent(c, "prime", DeviceEventType.Priming));
            placed = true;
        }

        if (c.Provenance?.RawAlarm is { Length: > 0 } alarm)
        {
            batch.SystemEvents.Add(new SystemEvent
            {
                OriginalId = Key(c.Id, "alarm"),
                EventType = SystemEventType.Alarm,
                Category = SystemEventCategory.Pump,
                Code = alarm,
                Description = AlarmDescription(alarm),
                Mills = new DateTimeOffset(c.Timestamp).ToUnixTimeMilliseconds(),
                Source = DataSources.GlookoConnector,
                Metadata = new Dictionary<string, object> { ["device"] = c.Device },
                CreatedAt = c.Now,
            });
            placed = true;
        }

        if (c.Provenance?.Wizard is { } wizard)
        {
            // recommended.net is what the calculator proposed; detail.total_value is what the
            // patient confirmed and the pump delivered. When they differ the patient overrode the
            // suggestion, whatever suggestion_overridden says — the flag has been seen at "no" on
            // an override. The bolus itself is a separate record at the same second and is not
            // pointed at this calculation: a re-read upserts the calculation under its legacy id
            // and keeps the stored row's Id, so a fresh Id on the bolus would break the foreign key.
            batch.BolusCalculations.Add(new BolusCalculation
            {
                Id = Guid.CreateVersion7(),
                Timestamp = c.Timestamp,
                LegacyId = Key(c.Id, "wizard"),
                Device = c.Device,
                DataSource = DataSources.GlookoConnector,
                BloodGlucoseInput = wizard.BgInput is { } bg ? GlookoXtGlucoseUnits.ToMgdl(bg, c.Unit) : null,
                CarbInput = wizard.CarbInput,
                InsulinRecommendation = wizard.RecommendedNet ?? wizard.Total,
                InsulinRecommendationForCarbs = wizard.RecommendedCarb,
                InsulinProgrammed = wizard.Total,
                EnteredInsulin = wizard.Total,
                CalculationType = CalculationType.Suggested,
                CreatedAt = c.Now,
                ModifiedAt = c.Now,
            });
            placed = true;
        }

        var noteText = NoteText(r, c.Provenance, placed);
        if (noteText is not null)
        {
            batch.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(),
                Timestamp = c.Timestamp,
                LegacyId = Key(c.Id, "note"),
                SyncIdentifier = Key(c.Id, "note"),
                Device = c.Device,
                DataSource = DataSources.GlookoConnector,
                Text = noteText,
                CreatedAt = c.Now,
                ModifiedAt = c.Now,
            });
            placed = true;
        }

        if (!placed)
        {
            _logger.LogDebug("Glooko XT record {Id} carried nothing the connector imports", c.Id);
            batch.Skipped++;
        }
    }

    /// <summary>
    ///     Turns the stream of undated basal changes into spans: each rate runs until the next
    ///     change, the last one is left open. Two changes at the same instant are one change — a
    ///     double upload — and the later id wins, so a zero-length span never reaches the tenant.
    ///     A zero rate is a real span (the loop withholding basal, or a suspension), not a gap.
    /// </summary>
    private static void ChainStreamBasals(
        List<(long Id, DateTime At, GlookoXtRecord Record, GlookoXtProvenance? Provenance, string Device)> stream,
        GlookoXtMappedBatch batch,
        DateTime now)
    {
        if (stream.Count == 0) return;

        var ordered = stream
            .OrderBy(s => s.At).ThenBy(s => s.Id)
            .GroupBy(s => s.At)
            .Select(g => g.Last())
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var (id, at, record, provenance, device) = ordered[i];
            DateTime? end = i + 1 < ordered.Count ? ordered[i + 1].At : null;
            var ctx = new Context(id, at, GlookoXtGlucoseUnits.Unit.MgDl, now, device, provenance);
            batch.TempBasals.Add(NewTempBasal(ctx, record, end));
        }
    }

    private static TempBasal NewTempBasal(Context c, GlookoXtRecord r, DateTime? end) => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = c.Timestamp,
        EndTimestamp = end,
        LegacyId = Key(c.Id, "basal"),
        SyncIdentifier = Key(c.Id, "basal"),
        Device = c.Device,
        DataSource = DataSources.GlookoConnector,
        Rate = r.BasalRate!.Value,
        Origin = BasalOrigin(r, c.Provenance),
        CreatedAt = c.Now,
        ModifiedAt = c.Now,
    };

    /// <summary>
    ///     Who set this rate. A suspended pump is its own origin. A percentage beside the rate is
    ///     the app's marker for an automated change, and a closed-loop source drives every rate it
    ///     reports; another synced pump's stream is its schedule; a rate typed into the app is
    ///     manual.
    /// </summary>
    private static TempBasalOrigin BasalOrigin(GlookoXtRecord r, GlookoXtProvenance? provenance)
    {
        if (r.PumpStop == true) return TempBasalOrigin.Suspended;
        if (r.RatePercentage is not null) return TempBasalOrigin.Algorithm;
        if (provenance is null) return TempBasalOrigin.Manual;
        return provenance.IsClosedLoop ? TempBasalOrigin.Algorithm : TempBasalOrigin.Scheduled;
    }

    /// <summary>
    ///     The text worth keeping as a standalone note. A note on a record that already yielded a
    ///     bolus or carbs is that record's label — "Meal Bolus", the marker for a micro bolus — and
    ///     would only clutter the timeline as a second entry. A record that carries nothing but text,
    ///     or a medication line, is the patient's own logbook entry and is kept. A provenance blob
    ///     in the memo is never text.
    /// </summary>
    private static string? NoteText(GlookoXtRecord r, GlookoXtProvenance? provenance, bool alreadyPlaced)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(r.MedicationName))
        {
            parts.Add(r.MedicationValue is { } value
                ? $"{r.MedicationName.Trim()} {value.ToString("0.##", CultureInfo.InvariantCulture)}"
                : r.MedicationName.Trim());
        }

        if (!alreadyPlaced)
        {
            var memo = provenance is null ? r.Memo : null;
            if (!string.IsNullOrWhiteSpace(r.Note)) parts.Add(r.Note.Trim());
            if (!string.IsNullOrWhiteSpace(memo) && !string.Equals(memo.Trim(), r.Note?.Trim(), StringComparison.Ordinal))
                parts.Add(memo.Trim());
            if (!string.IsNullOrWhiteSpace(r.MealDescription)) parts.Add(r.MealDescription.Trim());
        }

        return parts.Count == 0 ? null : string.Join(" — ", parts);
    }

    private static Bolus NewBolus(Context c, string facet, double units, GlookoXtRecord r)
    {
        var isSmb = string.Equals(r.Note?.Trim(), SmbMarker, StringComparison.OrdinalIgnoreCase);
        var extended = r.Duration is > 0 && r.BolusRate is not null;

        return new Bolus
        {
            Id = Guid.CreateVersion7(),
            Timestamp = c.Timestamp,
            LegacyId = Key(c.Id, facet),
            SyncIdentifier = Key(c.Id, facet),
            Device = c.Device,
            DataSource = DataSources.GlookoConnector,
            Insulin = units,
            Programmed = units,
            Delivered = units,
            BolusType = extended ? BolusType.Square : BolusType.Normal,
            Duration = extended ? r.Duration : null,
            Kind = isSmb ? BolusKind.Algorithm : BolusKind.Manual,
            Automatic = isSmb,
            CreatedAt = c.Now,
            ModifiedAt = c.Now,
        };
    }

    private static DeviceEvent NewDeviceEvent(Context c, string facet, DeviceEventType type) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Timestamp = c.Timestamp,
            LegacyId = Key(c.Id, facet),
            SyncIdentifier = Key(c.Id, facet),
            Device = c.Device,
            DataSource = DataSources.GlookoConnector,
            EventType = type,
            Notes = c.Provenance?.RawAlarm,
            CreatedAt = c.Now,
            ModifiedAt = c.Now,
        };

    /// <summary><c>alarm_occlusion</c> reads as "Occlusion": the vendor's code, less its prefix, as words.</summary>
    public static string AlarmDescription(string code)
    {
        var body = code.StartsWith("alarm_", StringComparison.OrdinalIgnoreCase) ? code[6..] : code;
        body = body.Replace('_', ' ').Trim();
        return body.Length == 0 ? code : char.ToUpperInvariant(body[0]) + body[1..];
    }

    public static string Key(long id, string facet) => $"glookoxt_{id}_{facet}";

    /// <summary>
    ///     <c>recorded_at</c> as UTC. The app writes ISO-8601 with a <c>Z</c>; a value with another
    ///     offset is honoured, and one with none is taken as UTC because that is the only zone the
    ///     server ever writes.
    /// </summary>
    public static bool TryParseTimestamp(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;

        utc = parsed.UtcDateTime;
        return true;
    }
}

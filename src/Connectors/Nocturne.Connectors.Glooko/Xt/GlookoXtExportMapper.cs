using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     Turns export rows into what the record stream cannot carry: pump-mode spans, boost and
///     ease-off overrides, safety alerts, consumable changes and the profile the pump settings
///     describe. Rows have no server id, so every key is built from the row's minute, its event
///     and its duration — two mode changes in one minute differ by duration, which is how the
///     pump reports a retry.
/// </summary>
public class GlookoXtExportMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private const string ModePrefix = "pumpMode - ";
    private const string OverridePrefix = "pumpSettingsOverride - ";
    private const string SafetyAlertPrefix = "Alerte de sécurité - ";

    /// <summary>
    ///     Doses, readings and the occlusion alarm arrive through the record stream with exact
    ///     seconds and a server id; the export repeats them at minute precision. Skipped here so
    ///     they are not stored twice.
    /// </summary>
    private static readonly HashSet<string> CarriedByRecords = new(StringComparer.OrdinalIgnoreCase) { "AlarmOcclusion" };

    /// <summary>Rows that describe nothing the tenant keeps: daily totals and the settings markers themselves.</summary>
    private static readonly string[] IgnoredEventPrefixes = ["info - ", "cgm settings", "pump settings"];

    public GlookoXtMappedBatch Map(GlookoXtExport export, string? unitSetting, GlookoXtMappedBatch? into = null) =>
        Map([export], unitSetting, into);

    /// <summary>
    ///     Maps every row of every export in one pass, so a row two windows both returned — the
    ///     windows are asked for by calendar day and a day of slack pads each side — lands once.
    /// </summary>
    public GlookoXtMappedBatch Map(IEnumerable<GlookoXtExport> exports, string? unitSetting, GlookoXtMappedBatch? into = null)
    {
        var batch = into ?? new GlookoXtMappedBatch();
        var now = DateTime.UtcNow;
        var glucoseValues = new List<double>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in exports.SelectMany(e => e.Rows))
        {
            if (row.SettingsJson is not null && MapSettings(row, batch, now, seen))
                continue;

            if (string.IsNullOrWhiteSpace(row.Event)) continue;
            var ev = row.Event.Trim();

            if (IgnoredEventPrefixes.Any(p => ev.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (CarriedByRecords.Contains(ev)) continue;

            if (ev.StartsWith(ModePrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddSpan(batch, seen, row, "mode", ev[ModePrefix.Length..], ModeState(ev[ModePrefix.Length..]), now);
                continue;
            }

            if (ev.StartsWith(OverridePrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddOverride(batch, seen, row, ev[OverridePrefix.Length..], now);
                continue;
            }

            if (ev.StartsWith(SafetyAlertPrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddSafetyAlert(batch, seen, row, ev[SafetyAlertPrefix.Length..], unitSetting, glucoseValues, now);
                continue;
            }

            switch (ev)
            {
                case "ReservoirChange":
                    AddDeviceEvent(batch, seen, row, DeviceEventType.ReservoirChange, now);
                    break;
                case "SiteSetChange":
                    AddDeviceEvent(batch, seen, row, DeviceEventType.SiteChange, now);
                    break;
                default:
                    if (ev.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
                        AddPumpAlarm(batch, seen, row, ev, now);
                    else
                        _logger.LogDebug("Glooko XT export event {Event} is not mapped", ev);
                    break;
            }
        }

        InferManualSpans(batch, now);

        return batch;
    }

    /// <summary>
    ///     A closed loop reports the time it runs and the time it tries to; it says nothing about
    ///     the time it is switched off, which is when the pump delivers on its own. Those gaps
    ///     between reported loop spans are the manual mode, filled in here so the timeline shows
    ///     every hour in a mode. A gap shorter than a minute is the export's minute rounding, not a
    ///     mode change.
    /// </summary>
    private static void InferManualSpans(GlookoXtMappedBatch batch, DateTime now)
    {
        var loop = batch.StateSpans
            .Where(s => s.Category == StateSpanCategory.PumpMode
                        && s.State is nameof(PumpModeState.Automatic) or nameof(PumpModeState.Limited) or nameof(PumpModeState.Manual)
                        or nameof(PumpModeState.Suspended) or nameof(PumpModeState.Off)
                        && s.OriginalId is not null && s.OriginalId.Contains("_mode_", StringComparison.Ordinal))
            .OrderBy(s => s.StartTimestamp)
            .ToList();
        if (loop.Count < 2) return;

        var coveredUntil = loop[0].EndTimestamp ?? loop[0].StartTimestamp;
        foreach (var span in loop.Skip(1))
        {
            var gap = span.StartTimestamp - coveredUntil;
            if (gap > TimeSpan.FromMinutes(1))
            {
                var key = $"glookoxt_export_mode_{new DateTimeOffset(coveredUntil).ToUnixTimeSeconds()}_{(long)gap.TotalMilliseconds}_manual_inferred";
                batch.StateSpans.Add(new StateSpan
                {
                    OriginalId = key,
                    Category = StateSpanCategory.PumpMode,
                    State = PumpModeState.Manual.ToString(),
                    StartTimestamp = coveredUntil,
                    EndTimestamp = span.StartTimestamp,
                    Source = DataSources.GlookoConnector,
                    CreatedAt = now,
                    Metadata = new Dictionary<string, object>
                    {
                        ["label"] = "Manual (between loop spans)",
                        ["durationSeconds"] = (long)gap.TotalSeconds,
                        ["inferred"] = true,
                    },
                });
            }

            var end = span.EndTimestamp ?? span.StartTimestamp;
            if (end > coveredUntil) coveredUntil = end;
        }
    }

    /// <summary>
    ///     CamAPS names its states in the export; the loop running is automatic, the loop trying to
    ///     resume — no sensor, no pump contact — is limited, and anything that says manual, open or
    ///     stopped is the pump on its own. A name not seen before is limited rather than dropped:
    ///     the span still says the loop was not fully in charge, and the label keeps the raw word.
    /// </summary>
    public static PumpModeState ModeState(string label)
    {
        var l = label.Trim().ToLowerInvariant();
        if (l.Contains("closed")) return PumpModeState.Automatic;
        if (l.Contains("attempt")) return PumpModeState.Limited;
        if (l.Contains("manual") || l.Contains("open") || l.Contains("standalone")) return PumpModeState.Manual;
        if (l.Contains("suspend") || l.Contains("stop")) return PumpModeState.Suspended;
        if (l == "off") return PumpModeState.Off;
        return PumpModeState.Limited;
    }

    /// <summary>
    ///     The name an override is shown under. Boost and Ease-off are temporary adjustments the
    ///     patient layers over the running mode, not modes themselves, so they are filed as
    ///     overrides beside the loop's mode span rather than replacing it.
    /// </summary>
    public static string OverrideName(string label)
    {
        var l = label.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return l switch
        {
            "boost" => "Boost",
            "easeoff" => "Ease-off",
            _ => label.Trim(),
        };
    }

    private static void AddOverride(GlookoXtMappedBatch batch, HashSet<string> seen, GlookoXtExportRow row, string label, DateTime now)
    {
        var key = Key(row, "override", label);
        if (!seen.Add(key)) return;

        batch.StateSpans.Add(new StateSpan
        {
            OriginalId = key,
            Category = StateSpanCategory.Override,
            State = Nocturne.Core.Models.OverrideState.Custom.ToString(),
            StartTimestamp = row.TimestampUtc,
            EndTimestamp = row.DurationMs is > 0 ? row.TimestampUtc.AddMilliseconds(row.DurationMs.Value) : null,
            Source = DataSources.GlookoConnector,
            CreatedAt = now,
            Metadata = new Dictionary<string, object>
            {
                ["name"] = OverrideName(label),
                ["label"] = label,
                ["durationSeconds"] = (row.DurationMs ?? 0) / 1000,
                ["device"] = row.PumpDevice ?? string.Empty,
            },
        });
    }

    private static void AddSpan(GlookoXtMappedBatch batch, HashSet<string> seen, GlookoXtExportRow row, string facet, string label, PumpModeState state, DateTime now)
    {
        var key = Key(row, facet, label);
        if (!seen.Add(key)) return;

        batch.StateSpans.Add(new StateSpan
        {
            OriginalId = key,
            Category = StateSpanCategory.PumpMode,
            State = state.ToString(),
            StartTimestamp = row.TimestampUtc,
            EndTimestamp = row.DurationMs is > 0 ? row.TimestampUtc.AddMilliseconds(row.DurationMs.Value) : null,
            Source = DataSources.GlookoConnector,
            CreatedAt = now,
            Metadata = new Dictionary<string, object>
            {
                ["label"] = label,
                ["durationSeconds"] = (row.DurationMs ?? 0) / 1000,
                ["device"] = row.PumpDevice ?? string.Empty,
            },
        });
    }

    /// <summary>
    ///     "Hypoglycémie : 69.06 mg/dl" — the app's language names the kind, the number is the
    ///     reading. Kind is told apart by the word's stem so the French and English exports read
    ///     the same; the reading is kept in the unit the export names.
    /// </summary>
    private void AddSafetyAlert(GlookoXtMappedBatch batch, HashSet<string> seen, GlookoXtExportRow row, string detail, string? unitSetting, List<double> glucoseValues, DateTime now)
    {
        var key = Key(row, "alert", detail);
        if (!seen.Add(key)) return;

        var lower = detail.ToLowerInvariant();
        var isLow = lower.Contains("hypo");
        var isHigh = lower.Contains("hyper");
        var code = isLow ? "safety_low" : isHigh ? "safety_high" : "safety_alert";

        var metadata = new Dictionary<string, object> { ["label"] = detail };
        var colon = detail.IndexOf(':');
        if (colon >= 0)
        {
            var number = new string(detail[(colon + 1)..].TrimStart().TakeWhile(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Replace(',', '.');
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                var unit = lower.Contains("mmol") ? GlookoXtGlucoseUnits.Unit.Mmol
                    : lower.Contains("mg") ? GlookoXtGlucoseUnits.Unit.MgDl
                    : GlookoXtGlucoseUnits.Resolve(unitSetting, [value]);
                metadata["mgdl"] = GlookoXtGlucoseUnits.ToMgdl(value, unit);
            }
        }

        batch.SystemEvents.Add(new SystemEvent
        {
            OriginalId = key,
            EventType = isLow ? SystemEventType.Hazard : SystemEventType.Warning,
            Category = SystemEventCategory.Cgm,
            Code = code,
            Description = detail,
            Mills = new DateTimeOffset(row.TimestampUtc).ToUnixTimeMilliseconds(),
            Source = DataSources.GlookoConnector,
            Metadata = metadata,
            CreatedAt = now,
        });
    }

    /// <summary>
    ///     Glooko XT reduces every pump alarm it does not classify to the word <c>AlarmOther</c>:
    ///     the export carries no text, code or value beside it, and no other call returns the
    ///     device's alarm log. Kept as information rather than a warning, since nothing says what
    ///     the pump wanted, with a description that says so.
    /// </summary>
    private static void AddPumpAlarm(GlookoXtMappedBatch batch, HashSet<string> seen, GlookoXtExportRow row, string ev, DateTime now)
    {
        var key = Key(row, "alarm", ev);
        if (!seen.Add(key)) return;

        var unclassified = ev.Equals("AlarmOther", StringComparison.OrdinalIgnoreCase);
        batch.SystemEvents.Add(new SystemEvent
        {
            OriginalId = key,
            EventType = unclassified ? SystemEventType.Info : SystemEventType.Warning,
            Category = SystemEventCategory.Pump,
            Code = ev,
            Description = unclassified
                ? "Pump alarm (type not reported by Glooko XT)"
                : GlookoXtRecordMapper.AlarmDescription(ev),
            Mills = new DateTimeOffset(row.TimestampUtc).ToUnixTimeMilliseconds(),
            Source = DataSources.GlookoConnector,
            Metadata = new Dictionary<string, object> { ["device"] = row.PumpDevice ?? string.Empty },
            CreatedAt = now,
        });
    }

    private static void AddDeviceEvent(GlookoXtMappedBatch batch, HashSet<string> seen, GlookoXtExportRow row, DeviceEventType type, DateTime now)
    {
        var key = Key(row, "device", type.ToString());
        if (!seen.Add(key)) return;

        batch.DeviceEvents.Add(new Nocturne.Core.Models.V4.DeviceEvent
        {
            Id = Guid.CreateVersion7(),
            Timestamp = row.TimestampUtc,
            LegacyId = key,
            SyncIdentifier = key,
            Device = row.PumpDevice ?? DataSources.GlookoConnector,
            DataSource = DataSources.GlookoConnector,
            EventType = type,
            CreatedAt = now,
            ModifiedAt = now,
        });
    }

    /// <summary>
    ///     The settings snapshot as a profile: one store entry per schedule id, the active schedule
    ///     as default, the pump's target band on every entry, and insulin action time as DIA.
    ///     Glucose values are in the unit the snapshot names.
    /// </summary>
    private bool MapSettings(GlookoXtExportRow row, GlookoXtMappedBatch batch, DateTime now, HashSet<string> seen)
    {
        var settings = GlookoXtPumpSettings.TryParse(row.SettingsJson);
        if (settings is null) return false;

        var timestamp = settings.Time ?? row.TimestampUtc;
        var mills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
        var id = $"glookoxt_settings_{mills}";
        if (!seen.Add(id)) return true;

        var unit = string.Equals(settings.BgUnit, "mmol/l", StringComparison.OrdinalIgnoreCase) || string.Equals(settings.BgUnit, "mmol", StringComparison.OrdinalIgnoreCase)
            ? GlookoXtGlucoseUnits.Unit.Mmol
            : GlookoXtGlucoseUnits.Unit.MgDl;
        var dia = settings.IobDurationSeconds is > 0 ? settings.IobDurationSeconds.Value / 3600.0 : 3.0;

        var store = new Dictionary<string, ProfileData>();
        ProfileData Data(string name)
        {
            if (!store.TryGetValue(name, out var d))
                store[name] = d = new ProfileData { Dia = dia, Units = "mg/dL", Timezone = null };
            return d;
        }

        foreach (var (name, segs) in settings.BasalSchedules)
            Data(name).Basal = segs.Select(s => TimeValue(s.StartMs, s.Value)).ToList();
        foreach (var (name, segs) in settings.CarbRatios)
            Data(name).CarbRatio = segs.Select(s => TimeValue(s.StartMs, s.Value)).ToList();
        foreach (var (name, segs) in settings.InsulinSensitivities)
            Data(name).Sens = segs.Select(s => TimeValue(s.StartMs, GlookoXtGlucoseUnits.ToMgdl(s.Value, unit))).ToList();
        foreach (var (name, segs) in settings.GlucoseTargets)
        {
            var d = Data(name);
            d.TargetLow = segs.Select(s => TimeValue(s.StartMs, GlookoXtGlucoseUnits.ToMgdl(s.Value, unit))).ToList();
            d.TargetHigh = segs.Select(s => TimeValue(s.StartMs, GlookoXtGlucoseUnits.ToMgdl(s.Value, unit))).ToList();
        }

        if (store.Count == 0)
        {
            _logger.LogDebug("Glooko XT settings snapshot at {Time:O} carried no schedules", timestamp);
            return true;
        }

        // The pump's own low/high band outranks a single-value target: it is what the loop steers within.
        if (settings.GlucoseTargetLow is { } low && settings.GlucoseTargetHigh is { } high)
        {
            foreach (var d in store.Values)
            {
                d.TargetLow = [TimeValue(0, GlookoXtGlucoseUnits.ToMgdl(low, unit))];
                d.TargetHigh = [TimeValue(0, GlookoXtGlucoseUnits.ToMgdl(high, unit))];
            }
        }

        var defaultProfile = settings.ActiveSchedule is { } active && store.ContainsKey(active) ? active : store.Keys.First();

        batch.Profiles.Add(new Profile
        {
            Id = id,
            DefaultProfile = defaultProfile,
            StartDate = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Mills = mills,
            CreatedAt = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Units = "mg/dL",
            EnteredBy = settings.Model ?? "Glooko XT",
            IsExternallyManaged = true,
            Store = store,
        });
        return true;
    }

    private static TimeValue TimeValue(int startMs, double value)
    {
        var seconds = Math.Clamp(startMs / 1000, 0, 86399);
        return new TimeValue
        {
            Time = $"{seconds / 3600:D2}:{seconds % 3600 / 60:D2}",
            Value = value,
            TimeAsSeconds = seconds,
        };
    }

    public static string Key(GlookoXtExportRow row, string facet, string label)
    {
        var seconds = new DateTimeOffset(row.TimestampUtc).ToUnixTimeSeconds();
        var slug = new string(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return $"glookoxt_export_{facet}_{seconds}_{row.DurationMs ?? 0}_{slug}";
    }
}

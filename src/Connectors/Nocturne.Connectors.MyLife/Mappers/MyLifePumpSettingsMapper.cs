using System.Globalization;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

internal static class MyLifePumpSettingsMapper
{
    /// <summary>
    /// Maps MyLife pump settings readouts to Nightscout Profile records.
    /// Each readout becomes one Profile with its basal programs mapped to named profiles in the store.
    /// </summary>
    internal static List<Profile> MapToProfiles(IReadOnlyList<MyLifePumpSettingsReadout> readouts)
    {
        var profiles = new List<Profile>();

        foreach (var readout in readouts)
        {
            if (readout.BasalPrograms == null || readout.BasalPrograms.Count == 0)
                continue;

            var uploadMills = MyLifeMapperHelpers.ToUnixMilliseconds(readout.UploadDateTime);
            var uploadDate = DateTimeOffset.FromUnixTimeMilliseconds(uploadMills);

            var store = new Dictionary<string, ProfileData>();
            foreach (var program in readout.BasalPrograms)
            {
                if (string.IsNullOrWhiteSpace(program.BasalProgramName))
                    continue;

                var basalSchedule = MapBasalEntries(program.BasalProgramEntries);

                store[program.BasalProgramName] = new ProfileData
                {
                    Basal = basalSchedule
                };
            }

            if (store.Count == 0)
                continue;

            var defaultProfile = !string.IsNullOrWhiteSpace(readout.ActiveBasalProgramName)
                && store.ContainsKey(readout.ActiveBasalProgramName)
                    ? readout.ActiveBasalProgramName
                    : store.Keys.First();

            profiles.Add(new Profile
            {
                Id = readout.Id,
                DefaultProfile = defaultProfile,
                StartDate = uploadDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Mills = uploadMills,
                CreatedAt = uploadDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Units = "mg/dL",
                Store = store,
                EnteredBy = "MyLife",
                IsExternallyManaged = true
            });
        }

        return profiles;
    }

    /// <summary>
    /// Maps MyLife pump settings readouts to Profile state_spans tracking the active basal program
    /// over time. Readouts are grouped by device serial number and sorted chronologically;
    /// consecutive readouts produce abutting spans, with the most recent left open-ended.
    /// Readouts with a null/empty ActiveBasalProgramName are skipped (the next readout still
    /// closes the prior span).
    /// </summary>
    internal static List<StateSpan> MapToStateSpans(
        IReadOnlyList<MyLifePumpSettingsReadout> readouts,
        string connectorSource)
    {
        var stateSpans = new List<StateSpan>();

        var byDevice = readouts.GroupBy(r => r.DeviceSerialNumber ?? string.Empty);

        foreach (var device in byDevice)
        {
            var ordered = device.OrderBy(r => r.UploadDateTime).ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var readout = ordered[i];
                var profileName = readout.ActiveBasalProgramName;
                if (string.IsNullOrWhiteSpace(profileName))
                    continue;

                var startMills = MyLifeMapperHelpers.ToUnixMilliseconds(readout.UploadDateTime);
                var startTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime;

                DateTime? endTimestamp = null;
                if (i < ordered.Count - 1)
                {
                    var nextMills = MyLifeMapperHelpers.ToUnixMilliseconds(ordered[i + 1].UploadDateTime);
                    endTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(nextMills).UtcDateTime;
                }

                var deviceKey = string.IsNullOrEmpty(device.Key) ? "unknown" : device.Key;
                stateSpans.Add(new StateSpan
                {
                    OriginalId = $"mylife_active_profile_{deviceKey}_{startMills}",
                    Category = StateSpanCategory.Profile,
                    State = ProfileState.Active.ToString(),
                    StartTimestamp = startTimestamp,
                    EndTimestamp = endTimestamp,
                    Source = connectorSource,
                    Metadata = new Dictionary<string, object>
                    {
                        { "profileName", profileName }
                    }
                });
            }
        }

        return stateSpans;
    }

    private static List<TimeValue> MapBasalEntries(List<MyLifeBasalProgramEntry>? entries)
    {
        if (entries == null || entries.Count == 0)
            return [];

        var result = new List<TimeValue>(entries.Count);
        foreach (var entry in entries)
        {
            if (!double.TryParse(entry.BasalRateUnitsPerHour, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                continue;

            var timeAsSeconds = entry.Hour * 3600 + entry.Minute * 60;
            result.Add(new TimeValue
            {
                Time = $"{entry.Hour:D2}:{entry.Minute:D2}",
                Value = rate,
                TimeAsSeconds = timeAsSeconds
            });
        }

        return result;
    }
}

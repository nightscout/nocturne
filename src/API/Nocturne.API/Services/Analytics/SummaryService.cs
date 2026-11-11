using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Aggregates recent DData and properties into the Nightscout summary response with 1:1 legacy
/// JavaScript compatibility. Delegates data retrieval to <see cref="IDDataService"/> and property
/// computation to <see cref="IPropertiesService"/>.
/// </summary>
/// <seealso cref="ISummaryService"/>
public class SummaryService : ISummaryService
{
    private readonly IDDataService _ddataService;
    private readonly IPropertiesService _propertiesService;
    private readonly IIobCobReader _iobCobReader;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITherapySettingsRepository _therapySettingsRepository;
    private readonly IProfileProjectionService _profileProjection;
    private readonly TimeProvider _time;
    private readonly ILogger<SummaryService> _logger;

    private static readonly JsonSerializerOptions StoredEntryOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] NullableStoredFields =
    [
        "perGIvalues",
        "carbs_hr_high",
        "carbs_hr_medium",
        "carbs_hr_low",
        "delay_high",
        "delay_medium",
        "delay_low",
    ];

    public SummaryService(
        IDDataService ddataService,
        IPropertiesService propertiesService,
        IIobCobReader iobCobReader,
        IActiveProfileResolver activeProfileResolver,
        ITherapySettingsRepository therapySettingsRepository,
        IProfileProjectionService profileProjection,
        TimeProvider timeProvider,
        ILogger<SummaryService> logger
    )
    {
        _time = timeProvider;
        _ddataService = ddataService;
        _propertiesService = propertiesService;
        _iobCobReader = iobCobReader;
        _activeProfileResolver = activeProfileResolver;
        _therapySettingsRepository = therapySettingsRepository;
        _profileProjection = profileProjection;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SummaryResponse> GetSummaryAsync(
        int hours = 6,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var currentTime = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var ddata = await _ddataService.GetDDataAsync(
                currentTime,
                DDataCollections.All,
                cancellationToken
            );
            var sgvs = ProcessSgvs(ddata.Sgvs, hours);
            var profileName = await _activeProfileResolver.GetProfileNameAsync(
                currentTime,
                cancellationToken
            );
            var profile = await ResolveActiveProfileAsync(profileName, cancellationToken);
            var treatments = ProcessTreatments(ddata.Treatments, profile, hours);
            var state = await ConstructStateAsync(
                ddata,
                await ReadIobCobAsync(cancellationToken),
                cancellationToken
            );

            return new SummaryResponse
            {
                Sgvs = sgvs,
                Treatments = treatments,
                Profile = ToStoredEntry(profile),
                State = state,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting summary data for {Hours} hours", hours);
            throw;
        }
    }

    /// <inheritdoc />
    public List<SummarySgv> ProcessSgvs(List<Entry> sgvs, int hours)
    {
        var bgData = new List<SummarySgv>();
        var dataCap = _time.GetUtcNow().ToUnixTimeMilliseconds() - (hours * 60 * 60 * 1000);

        foreach (var bg in sgvs.OrderBy(e => e.Mills))
        {
            if (bg.Mills >= dataCap)
            {
                var item = new SummarySgv { Sgv = (int)(bg.Mgdl), Mills = bg.Mills };

                // Only push noise data if there is noise and it's not 1
                if (bg.Noise.HasValue && bg.Noise != 1)
                {
                    item.Noise = bg.Noise;
                }

                bgData.Add(item);
            }
        }

        return bgData;
    }

    /// <inheritdoc />
    public SummaryTreatments ProcessTreatments(
        List<Treatment> treatments,
        ProfileData? profileData,
        int hours
    )
    {
        var result = new SummaryTreatments();
        var tempBasals = new List<Treatment>();
        var dataCap = _time.GetUtcNow().ToUnixTimeMilliseconds() - (hours * 60 * 60 * 1000);

        foreach (var treatment in treatments)
        {
            if (treatment.EventType == "Temp Basal")
            {
                tempBasals.Add(treatment);
                continue;
            }

            if (treatment.EventType == "Temporary Target")
            {
                result.Targets.Add(
                    new SummaryTarget
                    {
                        TargetTop = (int)
                            Math.Round(treatment.TargetTop ?? 0, MidpointRounding.AwayFromZero),
                        TargetBottom = (int)
                            Math.Round(treatment.TargetBottom ?? 0, MidpointRounding.AwayFromZero),
                        Duration = (int)((treatment.Duration ?? 0) * 60),
                        Mills = treatment.Mills,
                    }
                );
                continue;
            }

            if (
                (treatment.Insulin.HasValue && !double.IsNaN(treatment.Insulin.Value))
                || (treatment.Carbs.HasValue && !double.IsNaN(treatment.Carbs.Value))
            )
            {
                if (treatment.Mills >= dataCap)
                {
                    var summaryTreatment = new SummaryTreatment { Mills = treatment.Mills };

                    if (treatment.Carbs.HasValue && !double.IsNaN(treatment.Carbs.Value))
                    {
                        summaryTreatment.Carbs = treatment.Carbs.Value;
                    }

                    if (treatment.Insulin.HasValue && !double.IsNaN(treatment.Insulin.Value))
                    {
                        summaryTreatment.Insulin = treatment.Insulin.Value;
                    }

                    result.Treatments.Add(summaryTreatment);
                }
            }
        }

        result.Treatments = result.Treatments.OrderBy(t => t.Mills).ToList();

        result.TempBasals = ProcessTempBasals(profileData, tempBasals, dataCap);
        return result;
    }

    /// <inheritdoc />
    public List<SummaryTempBasal> ProcessTempBasals(
        ProfileData? profileData,
        List<Treatment> tempBasals,
        long dataCap
    )
    {
        if (profileData?.Basal == null || !profileData.Basal.Any())
        {
            return new List<SummaryTempBasal>();
        }

        var profileBasals = profileData.Basal;
        // NIGHTSCOUT-COMPAT: Nightscout reads only absolute here and reports a percent temp as 0 U/h, a
        // suspension. A percent temp reports the rate it delivers, and one whose rate is unknown is
        // left out so the profile basal shows through.
        var temps = tempBasals
            .Where(temp => temp.Percent is null || (temp.Absolute ?? temp.DeliveredRate) is not null)
            .Select(temp => new SummaryTempBasal
            {
                Start =
                    UploaderTimestamp.TryParse(temp.Created_at, out var createdAt)
                        ? createdAt.ToUnixTimeMilliseconds()
                        : temp.Mills,
                Duration = temp.Duration.HasValue ? (int)(temp.Duration.Value * 60 * 1000) : 0,
                Absolute = temp.Absolute ?? temp.DeliveredRate ?? 0,
            })
            .ToList();

        // Add boundary temps as per legacy implementation
        temps.Add(
            new SummaryTempBasal
            {
                Start = _time.GetUtcNow().ToUnixTimeMilliseconds() - 24 * 60 * 60 * 1000,
                Duration = 0,
                Absolute = 0,
            }
        );
        temps.Add(
            new SummaryTempBasal
            {
                Start = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                Duration = 0,
                Absolute = 0,
            }
        );

        temps = temps.OrderBy(t => t.Start).ToList();

        var output = new List<SummaryTempBasal>();
        foreach (var temp in temps)
        {
            var last = output.LastOrDefault();
            if (last != null && last.Duration > 0 && last.Start + last.Duration < temp.Start)
            {
                var profileBasalsInWindow = GetProfileBasalsInWindow(
                    profileBasals,
                    last.Start + last.Duration,
                    temp.Start
                );
                output.AddRange(profileBasalsInWindow);
            }
            if (temp.Duration > 0)
                output.Add(temp);
        }

        // Filter same absolute temps repeatedly until no changes
        var filteredOutput = output;
        int prevLength,
            newLength;
        do
        {
            prevLength = filteredOutput.Count;
            filteredOutput = FilterSameAbsoluteTemps(filteredOutput);
            newLength = filteredOutput.Count;
        } while (prevLength != newLength);

        // Return temps from last hours and convert durations to seconds
        var finalOutput = new List<SummaryTempBasal>();
        foreach (var temp in filteredOutput)
        {
            if ((temp.Start + temp.Duration) > dataCap)
            {
                var finalTemp = new SummaryTempBasal
                {
                    Start = temp.Start,
                    Duration = temp.Duration / 1000, // Convert to seconds
                    Absolute = temp.Absolute,
                    Profile = temp.Profile,
                };
                finalOutput.Add(finalTemp);
            }
        }

        return finalOutput;
    }

    private async Task<IobCobReading?> ReadIobCobAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _iobCobReader.ReadTotalAsync(
                _time.GetUtcNow().ToUnixTimeMilliseconds(),
                live: true,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error reading IOB and COB; writing both as null");
            return null;
        }
    }

    /// <summary>
    /// Whether the IOB/COB read windows hold a bolus, temp basal or carb intake. With a profile, this
    /// stands in for Nightscout's COB plugin having any treatment to read, so a profile with only
    /// insulin gives a COB of zero rather than none. Nightscout counts any treatment in about two
    /// days of data, so a site change alone gives zero there and none here.
    /// </summary>
    private static bool HasTreatments(IobCobInputs inputs) =>
        inputs.Boluses.Count > 0 || inputs.TempBasals.Count > 0 || inputs.CarbIntakes.Count > 0;

    private async Task<SummaryState> ConstructStateAsync(
        DData ddata,
        IobCobReading? reading,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var properties = await _propertiesService.GetAllPropertiesAsync(
                ddata,
                reading,
                cancellationToken
            );

            var state = new SummaryState();

            // Nightscout's summary uses Math.round, not the display's toFixed: 0.015 is 0.02 here and "0.01" there.
            if (reading?.HasIob == true)
                state.Iob = Math.Floor(reading.Iob.Iob * 100 + 0.5) / 100;
            if (reading?.Cob is { } cob && (reading.HasCob || (properties.ContainsKey("profile") && HasTreatments(reading.Inputs))))
                state.Cob = (int)Math.Floor(cob.Cob + 0.5);

            // Extract BWP (Bolus Wizard Preview)
            if (
                properties.TryGetValue("bwp", out var bwpObj)
                && bwpObj is Dictionary<string, object> bwpDict
            )
            {
                if (
                    bwpDict.TryGetValue("bolusEstimate", out var bwpValue)
                    && double.TryParse(bwpValue?.ToString(), out var bwp)
                )
                {
                    state.Bwp = Math.Floor(bwp * 100 + 0.5) / 100;
                }
            }

            // Extract device ages
            ExtractDeviceAge(properties, "cage", "age", value => state.Cage = value);
            ExtractDeviceAge(properties, "sage", "age", value => state.Sage = value);
            ExtractDeviceAge(properties, "iage", "age", value => state.Iage = value);
            ExtractDeviceAge(properties, "bage", "age", value => state.Bage = value);
            ExtractDeviceAge(properties, "calib", "age", value => state.CalibAge = value);

            // Calculate sensor expiration time (typical sensor life is 10-14 days, using 10 days = 240 hours)
            if (state.Sage.HasValue)
            {
                const int sensorLifeHours = 240; // 10 days in hours
                var expiresIn = sensorLifeHours - state.Sage.Value;
                state.SensorExpiresIn = expiresIn > 0 ? expiresIn : 0;
            }

            // Extract battery level
            if (
                properties.TryGetValue("upbat", out var upbatObj)
                && upbatObj is Dictionary<string, object> upbatDict
            )
            {
                if (
                    upbatDict.TryGetValue("level", out var batteryValue)
                    && int.TryParse(batteryValue?.ToString(), out var battery)
                )
                {
                    state.Battery = battery;
                }
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error constructing summary state");
            throw;
        }
    }

    /// <inheritdoc />
    public List<SummaryTempBasal> FilterSameAbsoluteTemps(List<SummaryTempBasal> tempData)
    {
        var output = new List<SummaryTempBasal>();
        int mergedIndex = -1;

        for (int i = 0; i < tempData.Count; i++)
        {
            var temp = tempData[i];

            if (i == tempData.Count - 1)
            {
                // If last was merged, skip
                if (mergedIndex != i)
                {
                    output.Add(temp);
                }
                break;
            }

            var nextTemp = tempData[i + 1];

            if (temp.Duration > 0 && (temp.Start + temp.Duration) >= nextTemp.Start)
            {
                if (Math.Abs(temp.Absolute - nextTemp.Absolute) < 0.001) // Compare doubles with tolerance
                {
                    // Merge and skip next
                    temp.Duration = (int)(nextTemp.Start - temp.Start + nextTemp.Duration);
                    i += 1;
                    mergedIndex = i;
                }
                else
                {
                    // Adjust duration
                    temp.Duration = (int)(nextTemp.Start - temp.Start);
                }
            }
            output.Add(temp);
        }

        return output;
    }

    /// <inheritdoc />
    public List<SummaryTempBasal> GetProfileBasalsInWindow(
        List<TimeValue>? basals,
        long start,
        long end
    )
    {
        if (basals == null || !basals.Any())
        {
            return new List<SummaryTempBasal>();
        }

        var output = new List<SummaryTempBasal>();
        int currentIndex = 0;

        // Find the appropriate basal rate for the start time
        var startTime = DateTimeOffset.FromUnixTimeMilliseconds(start);
        var startHHMM = startTime.ToString("HH:mm");

        while (
            currentIndex < basals.Count - 1
            && string.Compare(basals[currentIndex + 1].Time, startHHMM, StringComparison.Ordinal)
                <= 0
        )
        {
            currentIndex++;
        }

        output.Add(
            new SummaryTempBasal
            {
                Start = start,
                Absolute = basals[currentIndex].Value,
                Duration = 0, // Will be calculated
                Profile = 1,
            }
        );

        // Add subsequent basal rates within the window
        var nextIndex = (currentIndex + 1) % basals.Count;
        var nextBasalTime = GetNextBasalTime(basals[nextIndex].Time, start);

        while (nextBasalTime < end)
        {
            currentIndex = nextIndex;

            output.Add(
                new SummaryTempBasal
                {
                    Start = nextBasalTime,
                    Absolute = basals[currentIndex].Value,
                    Duration = 0, // Will be calculated
                    Profile = 1,
                }
            );

            nextIndex = (currentIndex + 1) % basals.Count;
            nextBasalTime = GetNextBasalTime(basals[nextIndex].Time, nextBasalTime);
        }

        return output;
    }

    /// <summary>
    /// The store entry read now (<see cref="IActiveProfileResolver.GetProfileNameAsync"/>), or the
    /// <see cref="Nocturne.Core.Models.V4.TherapySettings.IsDefault"/> row's when that store has no entry.
    /// Entries are assembled from their rows, not read out of the newest projected documents, which
    /// Profile Switch snapshots can crowd out.
    /// </summary>
    private async Task<ProfileData?> ResolveActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken
    )
    {
        var row = (
            await _therapySettingsRepository.GetByProfileNameAsync(profileName, cancellationToken)
        ).FirstOrDefault();
        if (await EntryOfAsync(row, cancellationToken) is { } entry)
            return entry;

        var defaults = await _therapySettingsRepository.GetDefaultsAsync(cancellationToken);
        return await EntryOfAsync(defaults.FirstOrDefault(), cancellationToken);
    }

    private async Task<ProfileData?> EntryOfAsync(
        Nocturne.Core.Models.V4.TherapySettings? row,
        CancellationToken cancellationToken
    )
    {
        if (row is null)
            return null;

        var document = await _profileProjection.GetProfileByIdAsync(row.Id.ToString(), cancellationToken);
        return document?.Store.GetValueOrDefault(row.ProfileName);
    }

    /// <summary>
    /// The entry as a raw JSON object, so the response's camelCase policy cannot rename its keys,
    /// without <c>timeAsSeconds</c>. An empty object when no profile is active. The GI fields
    /// are written as null when unset, as Nightscout stores them.
    /// </summary>
    private static JsonObject ToStoredEntry(ProfileData? profile)
    {
        if (profile is null)
            return new JsonObject();

        var entry = JsonSerializer.SerializeToNode(profile, StoredEntryOptions)!.AsObject();
        RemoveTimeAsSeconds(entry);
        foreach (var field in NullableStoredFields)
            entry.TryAdd(field, null);
        return entry;
    }

    private static void RemoveTimeAsSeconds(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("timeAsSeconds");
                foreach (var child in obj.Select(p => p.Value))
                    RemoveTimeAsSeconds(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    RemoveTimeAsSeconds(child);
                break;
        }
    }

    /// <summary>
    /// Extracts device age from properties dictionary
    /// </summary>
    private void ExtractDeviceAge(
        Dictionary<string, object> properties,
        string propertyName,
        string subPropertyName,
        Action<int?> setValue
    )
    {
        if (
            properties.TryGetValue(propertyName, out var deviceObj)
            && deviceObj is Dictionary<string, object> deviceDict
        )
        {
            if (
                deviceDict.TryGetValue(subPropertyName, out var ageValue)
                && int.TryParse(ageValue?.ToString(), out var age)
            )
            {
                setValue(age);
            }
        }
    }

    /// <summary>
    /// Gets the next basal time after the given timestamp
    /// Equivalent to legacy _hhmmAfter function
    /// </summary>
    private long GetNextBasalTime(string hhmm, long mills)
    {
        var date = DateTimeOffset.FromUnixTimeMilliseconds(mills);
        var hours = int.Parse(hhmm.Substring(0, 2));
        var minutes = int.Parse(hhmm.Substring(3, 2));

        var withSameDate = new DateTimeOffset(
            date.Year,
            date.Month,
            date.Day,
            hours,
            minutes,
            0,
            date.Offset
        );

        return withSameDate.ToUnixTimeMilliseconds() > mills
            ? withSameDate.ToUnixTimeMilliseconds()
            : withSameDate.AddDays(1).ToUnixTimeMilliseconds();
    }
}

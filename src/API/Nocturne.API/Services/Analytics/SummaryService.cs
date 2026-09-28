using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
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
    private readonly ILogger<SummaryService> _logger;

    public SummaryService(
        IDDataService ddataService,
        IPropertiesService propertiesService,
        ILogger<SummaryService> logger
    )
    {
        _ddataService = ddataService;
        _propertiesService = propertiesService;
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
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ddata = await _ddataService.GetDDataAsync(currentTime, cancellationToken);
            var sgvs = ProcessSgvs(ddata.Sgvs, hours);
            var profile = ddata.Profiles?.FirstOrDefault()?.Store?.FirstOrDefault().Value;
            var treatments = ProcessTreatments(ddata.Treatments, profile, hours);
            var state = await ConstructStateAsync(cancellationToken);

            // Remove timeAsSeconds property from profile to match legacy behavior
            var processedProfile = RemoveTimeAsSecondsFromProfile(profile);

            return new SummaryResponse
            {
                Sgvs = sgvs,
                Treatments = treatments,
                Profile = processedProfile,
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
        var dataCap = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (hours * 60 * 60 * 1000);

        foreach (var bg in sgvs)
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
        var dataCap = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (hours * 60 * 60 * 1000);

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
        var temps = tempBasals
            .Select(temp => new SummaryTempBasal
            {
                Start =
                    UploaderTimestamp.TryParse(temp.Created_at, out var createdAt)
                        ? createdAt.ToUnixTimeMilliseconds()
                        : temp.Mills,
                Duration = temp.Duration.HasValue ? (int)(temp.Duration.Value * 60 * 1000) : 0,
                Absolute = temp.Absolute ?? 0,
            })
            .ToList();

        // Add boundary temps as per legacy implementation
        temps.Add(
            new SummaryTempBasal
            {
                Start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 24 * 60 * 60 * 1000,
                Duration = 0,
                Absolute = 0,
            }
        );
        temps.Add(
            new SummaryTempBasal
            {
                Start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

    /// <inheritdoc />
    public async Task<SummaryState> ConstructStateAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var properties = await _propertiesService.GetAllPropertiesAsync(cancellationToken);

            var state = new SummaryState();

            // Extract IOB
            if (
                properties.TryGetValue("iob", out var iobObj)
                && iobObj is Dictionary<string, object> iobDict
            )
            {
                if (
                    iobDict.TryGetValue("iob", out var iobValue)
                    && double.TryParse(iobValue?.ToString(), out var iob)
                )
                {
                    state.Iob = Math.Round(iob * 100) / 100;
                }
            }

            // Extract COB
            if (
                properties.TryGetValue("cob", out var cobObj)
                && cobObj is Dictionary<string, object> cobDict
            )
            {
                if (
                    cobDict.TryGetValue("cob", out var cobValue)
                    && double.TryParse(cobValue?.ToString(), out var cob)
                )
                {
                    state.Cob = (int)Math.Round(cob);
                }
            }

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
                    state.Bwp = Math.Round(bwp * 100) / 100;
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
    /// Removes the timeAsSeconds property from profile to match legacy behavior
    /// </summary>
    private object? RemoveTimeAsSecondsFromProfile(ProfileData? profileData)
    {
        if (profileData == null)
            return null;

        // Create a simple clone without timeAsSeconds properties
        // This matches the legacy removeProps functionality
        return new
        {
            profileData.Dia,
            profileData.CarbsHr,
            profileData.Delay,
            profileData.Timezone,
            profileData.Units,
            profileData.DelayHigh,
            profileData.DelayLow,
            Basal = profileData.Basal?.Select(b => new { b.Time, b.Value }),
            CarbRatio = profileData.CarbRatio?.Select(c => new { c.Time, c.Value }),
            Sens = profileData.Sens?.Select(s => new { s.Time, s.Value }),
            TargetLow = profileData.TargetLow?.Select(t => new { t.Time, t.Value }),
            TargetHigh = profileData.TargetHigh?.Select(t => new { t.Time, t.Value }),
        };
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

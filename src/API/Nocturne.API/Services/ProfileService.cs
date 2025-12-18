using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// Full 1:1 legacy-compatible implementation of profile functions
/// Based on ClientApp/lib/profilefunctions.js with exact algorithm matching
/// </summary>
public class ProfileService : IProfileService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProfileService>? _logger;
    private const int CacheTtlMs = 5000; // 5 seconds cache TTL like legacy

    private List<Profile>? _profileData;
    private List<Treatment> _profileTreatments = new();
    private List<Treatment> _tempBasalTreatments = new();
    private List<Treatment> _comboBolusTreatments = new();
    private Treatment? _prevBasalTreatment;

    public ProfileService(IMemoryCache cache, ILogger<ProfileService>? logger = null)
    {
        _cache = cache;
        _logger = logger;
    }

    public void Clear()
    {
        // Note: IMemoryCache doesn't have a Clear method in .NET
        // Individual cache entries will expire based on TTL
        _profileData = null;
        _prevBasalTreatment = null;
        _profileTreatments.Clear();
        _tempBasalTreatments.Clear();
        _comboBolusTreatments.Clear();
    }

    public void LoadData(List<Profile> profileData)
    {
        if (profileData?.Any() == true)
        {
            _profileData = ConvertToProfileStore(profileData);

            // Process each profile and preprocess time values
            foreach (var record in _profileData)
            {
                if (record.Store?.Any() == true)
                {
                    foreach (var profile in record.Store.Values)
                    {
                        PreprocessProfileOnLoad(profile);
                    }
                }
                record.Mills = DateTimeOffset.Parse(record.StartDate).ToUnixTimeMilliseconds();
            }
        }
    }

    public bool HasData() => _profileData?.Any() == true;

    public Profile? GetCurrentProfile(long? time = null, string? specProfile = null)
    {
        time ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Round to the minute for better caching (like legacy)
        var minuteTime = (long)(Math.Round(time.Value / 60000.0) * 60000);
        var cacheKey = $"profile{minuteTime}{specProfile}";

        if (_cache.TryGetValue(cacheKey, out ProfileData? cachedResult))
        {
            return CreateProfileFromData(cachedResult);
        }

        var pdataActive = ProfileFromTime(time.Value);
        var data = HasData() ? pdataActive : null;
        var timeProfile = GetActiveProfileName(time.Value);

        var returnValue =
            data?.Store?.ContainsKey(timeProfile ?? "") == true
                ? data.Store[timeProfile!]
                : new ProfileData();

        _cache.Set(cacheKey, returnValue, TimeSpan.FromMilliseconds(CacheTtlMs));

        return CreateProfileFromData(returnValue);
    }

    public string? GetActiveProfileName(long? time = null)
    {
        if (!HasData())
            return null;

        time ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var pdataActive = ProfileFromTime(time.Value);
        var timeProfile = pdataActive?.DefaultProfile;
        var treatment = GetActiveProfileTreatment(time.Value);

        if (treatment != null && pdataActive?.Store?.ContainsKey(treatment.Profile ?? "") == true)
        {
            timeProfile = treatment.Profile;
        }

        return timeProfile;
    }

    public List<string> ListBasalProfiles()
    {
        var profiles = new List<string>();

        if (HasData())
        {
            var current = GetActiveProfileName();
            if (!string.IsNullOrEmpty(current))
            {
                profiles.Add(current);
            }

            var firstProfile = _profileData?.FirstOrDefault();
            if (firstProfile?.Store?.Any() == true)
            {
                foreach (var key in firstProfile.Store.Keys)
                {
                    if (key != current && !key.Contains("@@@@@"))
                    {
                        profiles.Add(key);
                    }
                }
            }
        }

        return profiles;
    }

    public string? GetUnits(string? specProfile = null)
    {
        var currentProfile = GetCurrentProfile(null, specProfile);
        var units = currentProfile?.Store?.Values.FirstOrDefault()?.Units ?? "";

        return units.ToLowerInvariant().Contains("mmol") ? "mmol" : "mg/dl";
    }

    public string? GetTimezone(string? specProfile = null)
    {
        var currentProfile = GetCurrentProfile(null, specProfile);
        var timezone = currentProfile?.Store?.Values.FirstOrDefault()?.Timezone;

        // Work around Loop uploading non-ISO compliant time zone string
        if (!string.IsNullOrEmpty(timezone))
        {
            timezone = timezone.Replace("ETC", "Etc");
        }

        return timezone;
    }

    public double GetValueByTime(long time, string valueType, string? specProfile = null)
    {
        // Round to the minute for better caching
        var minuteTime = (long)(Math.Round(time / 60000.0) * 60000);
        var cacheKey = $"{minuteTime}{valueType}{specProfile}";

        if (_cache.TryGetValue(cacheKey, out double cachedValue))
        {
            return cachedValue;
        }

        // CircadianPercentageProfile support
        var timeshift = 0.0;
        var percentage = 100.0;
        var activeTreatment = GetActiveProfileTreatment(time);
        var isCcpProfile =
            string.IsNullOrEmpty(specProfile)
            && activeTreatment?.CircadianPercentageProfile == true;

        if (isCcpProfile)
        {
            percentage = activeTreatment?.Percentage ?? 100.0;
            timeshift = activeTreatment?.Timeshift ?? 0.0; // in hours
        }

        var offset = timeshift % 24;
        var adjustedTime = time + (long)(offset * 3600000); // Convert hours to milliseconds

        var currentProfile = GetCurrentProfile(adjustedTime, specProfile);
        var profileData = currentProfile?.Store?.Values.FirstOrDefault();

        if (profileData == null)
        {
            return GetDefaultValue(valueType);
        }

        var valueContainer = GetValueContainer(profileData, valueType);

        // Convert time to seconds from midnight (like legacy)
        var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(minuteTime);
        var timeZone = GetTimezone(specProfile);

        if (!string.IsNullOrEmpty(timeZone))
        {
            try
            {
                var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                dateTime = TimeZoneInfo.ConvertTime(dateTime, timeZoneInfo);
            }
            catch
            {
                // Fall back to UTC if timezone conversion fails
            }
        }

        var midnight = dateTime.Date;
        var timeAsSecondsFromMidnight = (int)(dateTime - midnight).TotalSeconds;

        var returnValue = GetValueFromContainer(
            valueContainer,
            timeAsSecondsFromMidnight,
            valueType
        );

        // Apply CircadianPercentageProfile adjustments
        if (isCcpProfile && returnValue != 0)
        {
            switch (valueType)
            {
                case "sens":
                case "carbratio":
                    returnValue = returnValue * 100 / percentage;
                    break;
                case "basal":
                    returnValue = returnValue * percentage / 100;
                    break;
            }
        }

        _cache.Set(cacheKey, returnValue, TimeSpan.FromMilliseconds(CacheTtlMs));
        return returnValue;
    }

    // Specific profile value methods
    public double GetDIA(long time, string? specProfile = null) =>
        GetValueByTime(time, "dia", specProfile);

    public double GetSensitivity(long time, string? specProfile = null) =>
        GetValueByTime(time, "sens", specProfile);

    public double GetCarbRatio(long time, string? specProfile = null) =>
        GetValueByTime(time, "carbratio", specProfile);

    public double GetCarbAbsorptionRate(long time, string? specProfile = null) =>
        GetValueByTime(time, "carbs_hr", specProfile);

    public double GetLowBGTarget(long time, string? specProfile = null) =>
        GetValueByTime(time, "target_low", specProfile);

    public double GetHighBGTarget(long time, string? specProfile = null) =>
        GetValueByTime(time, "target_high", specProfile);

    public double GetBasalRate(long time, string? specProfile = null) =>
        GetValueByTime(time, "basal", specProfile);

    public void UpdateTreatments(
        List<Treatment>? profileTreatments = null,
        List<Treatment>? tempBasalTreatments = null,
        List<Treatment>? comboBolusTreatments = null
    )
    {
        _profileTreatments = profileTreatments ?? new List<Treatment>();
        _tempBasalTreatments = tempBasalTreatments ?? new List<Treatment>();
        _comboBolusTreatments = comboBolusTreatments ?? new List<Treatment>();

        // Dedupe temp basal events by mills (like legacy uniqBy)
        _tempBasalTreatments = _tempBasalTreatments
            .GroupBy(t => t.Mills)
            .Select(g => g.First())
            .ToList();

        // Add duration end mills for temp basals
        foreach (var treatment in _tempBasalTreatments)
        {
            var durationMs = (long)((treatment.Duration ?? 0) * 60000); // Convert minutes to milliseconds
            treatment.EndMills = treatment.Mills + durationMs;
        }

        // Sort by mills
        _tempBasalTreatments.Sort((a, b) => a.Mills.CompareTo(b.Mills));

        // Clear cache by creating a new instance (workaround for IMemoryCache not having Clear)
        // In practice, individual cache entries will expire naturally
    }

    public Treatment? GetActiveProfileTreatment(long time)
    {
        var minuteTime = (long)(Math.Round(time / 60000.0) * 60000);
        var cacheKey = $"profileCache{minuteTime}";

        if (_cache.TryGetValue(cacheKey, out Treatment? cachedTreatment))
        {
            return cachedTreatment;
        }

        Treatment? treatment = null;

        if (HasData())
        {
            var pdataActive = ProfileFromTime(time);

            foreach (var t in _profileTreatments)
            {
                if (time >= t.Mills && t.Mills >= (pdataActive?.Mills ?? 0))
                {
                    var durationMs = (t.Duration ?? 0) * 60000; // Convert minutes to milliseconds

                    if (durationMs != 0 && time < t.Mills + durationMs)
                    {
                        treatment = t;
                        HandleProfileJson(treatment, pdataActive);
                    }
                    else if (durationMs == 0)
                    {
                        treatment = t;
                        HandleProfileJson(treatment, pdataActive);
                    }
                }
            }
        }

        _cache.Set(cacheKey, treatment, TimeSpan.FromMilliseconds(CacheTtlMs));
        return treatment;
    }

    public Treatment? GetTempBasalTreatment(long time)
    {
        // Most queries will match the latest found value, caching improves performance
        if (
            _prevBasalTreatment != null
            && time >= _prevBasalTreatment.Mills
            && time <= (_prevBasalTreatment.EndMills ?? long.MaxValue)
        )
        {
            return _prevBasalTreatment;
        }

        // Binary search for O(log n) performance (like legacy)
        var first = 0;
        var last = _tempBasalTreatments.Count - 1;

        while (first <= last)
        {
            var i = first + (last - first) / 2;
            var t = _tempBasalTreatments[i];

            if (time >= t.Mills && time <= (t.EndMills ?? long.MaxValue))
            {
                _prevBasalTreatment = t;
                return t;
            }

            if (time < t.Mills)
            {
                last = i - 1;
            }
            else
            {
                first = i + 1;
            }
        }

        return null;
    }

    public Treatment? GetComboBolusTreatment(long time)
    {
        foreach (var t in _comboBolusTreatments)
        {
            var durationMs = (t.Duration ?? 0) * 60000; // Convert minutes to milliseconds
            if (time < t.Mills + durationMs && time > t.Mills)
            {
                return t;
            }
        }
        return null;
    }

    public TempBasalResult GetTempBasal(long time, string? specProfile = null)
    {
        var minuteTime = (long)(Math.Round(time / 60000.0) * 60000);
        var cacheKey = $"basalCache{minuteTime}{specProfile}";
        if (_cache.TryGetValue(cacheKey, out TempBasalResult? cachedResult) && cachedResult != null)
        {
            return cachedResult;
        }

        var basal = GetBasalRate(time, specProfile);
        var tempBasal = basal;
        var comboBolusBasal = 0.0;
        var treatment = GetTempBasalTreatment(time);
        var comboBolusTreatment = GetComboBolusTreatment(time);

        // Check for absolute temp basal rate (supports temp to 0)
        // Loop and other systems may use either 'Absolute' or 'Rate' field
        if (treatment != null && (treatment.Duration ?? 0) > 0)
        {
            if (treatment.Absolute.HasValue)
            {
                tempBasal = treatment.Absolute.Value;
            }
            else if (treatment.Rate.HasValue)
            {
                tempBasal = treatment.Rate.Value;
            }
            else if (treatment.Percent.HasValue)
            {
                tempBasal = basal * (100 + treatment.Percent.Value) / 100;
            }
            else if (treatment.Amount.HasValue)
            {
                // Fallback for systems using 'Amount' instead of 'Rate'
                tempBasal = treatment.Amount.Value;
            }
            else if (treatment.Insulin.HasValue && treatment.Duration.Value > 0)
            {
                // Fallback: calculate rate from total insulin and duration (in minutes)
                // Rate (U/hr) = Total Insulin (U) / (Duration (min) / 60)
                tempBasal = treatment.Insulin.Value / (treatment.Duration.Value / 60.0);
            }
        }

        if (comboBolusTreatment?.Relative.HasValue == true)
        {
            comboBolusBasal = comboBolusTreatment.Relative.Value;
        }

        var result = new TempBasalResult
        {
            Basal = basal,
            Treatment = treatment,
            ComboBolusTreatment = comboBolusTreatment,
            TempBasal = tempBasal,
            ComboBolusBasal = comboBolusBasal,
            TotalBasal = tempBasal + comboBolusBasal,
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMilliseconds(CacheTtlMs));
        return result;
    }

    // Private helper methods
    private List<Profile> ConvertToProfileStore(List<Profile> dataArray)
    {
        var convertedProfiles = new List<Profile>();

        foreach (var profile in dataArray)
        {
            if (string.IsNullOrEmpty(profile.DefaultProfile))
            {
                var newProfile = new Profile
                {
                    DefaultProfile = "Default",
                    Store = new Dictionary<string, ProfileData>(),
                    StartDate = !string.IsNullOrEmpty(profile.StartDate)
                        ? profile.StartDate
                        : "1980-01-01",
                    Id = profile.Id,
                    ConvertedOnTheFly = true,
                };

                // Move profile data to Default store entry
                var sourceData = profile.Store?.Values.FirstOrDefault();
                var defaultData = new ProfileData
                {
                    Dia = sourceData?.Dia ?? 3.0,
                    CarbsHr = sourceData?.CarbsHr ?? 20,
                    Timezone = sourceData?.Timezone,
                    Units = sourceData?.Units,
                    Basal = sourceData?.Basal ?? new List<TimeValue>(),
                    CarbRatio = sourceData?.CarbRatio ?? new List<TimeValue>(),
                    Sens = sourceData?.Sens ?? new List<TimeValue>(),
                    TargetLow = sourceData?.TargetLow ?? new List<TimeValue>(),
                    TargetHigh = sourceData?.TargetHigh ?? new List<TimeValue>(),
                };

                newProfile.Store["Default"] = defaultData;
                convertedProfiles.Add(newProfile);

                _logger?.LogDebug(
                    "Profile not updated yet. Converted profile: {ProfileId}",
                    newProfile.Id
                );
            }
            else
            {
                // Remove conversion flag if present
                profile.ConvertedOnTheFly = false;
                convertedProfiles.Add(profile);
            }
        }

        return convertedProfiles;
    }

    private void PreprocessProfileOnLoad(ProfileData profileData)
    {
        // Convert time strings to seconds for faster operations
        PreprocessTimeValues(profileData.Basal);
        PreprocessTimeValues(profileData.CarbRatio);
        PreprocessTimeValues(profileData.Sens);
        PreprocessTimeValues(profileData.TargetLow);
        PreprocessTimeValues(profileData.TargetHigh);
    }

    private void PreprocessTimeValues(List<TimeValue>? timeValues)
    {
        if (timeValues == null)
            return;

        foreach (var timeValue in timeValues)
        {
            if (!string.IsNullOrEmpty(timeValue.Time))
            {
                var seconds = TimeStringToSeconds(timeValue.Time);
                if (seconds >= 0)
                {
                    timeValue.TimeAsSeconds = seconds;
                }
            }
        }
    }

    private int TimeStringToSeconds(string time)
    {
        var parts = time.Split(':');
        if (
            parts.Length >= 2
            && int.TryParse(parts[0], out var hours)
            && int.TryParse(parts[1], out var minutes)
        )
        {
            return hours * 3600 + minutes * 60;
        }
        return -1;
    }

    private Profile? ProfileFromTime(long time)
    {
        if (!HasData())
            return null;

        Profile? profileData = _profileData![0];

        // Find the most recent profile that started before or at the given time
        foreach (var profile in _profileData!)
        {
            if (time >= profile.Mills)
            {
                profileData = profile;
            }
            else
            {
                // Profiles are assumed to be sorted by Mills, so we can break early
                break;
            }
        }

        return profileData;
    }

    private object? GetValueContainer(ProfileData profileData, string valueType)
    {
        return valueType switch
        {
            "dia" => profileData.Dia,
            "sens" => profileData.Sens,
            "carbratio" => profileData.CarbRatio,
            "carbs_hr" => profileData.CarbsHr,
            "target_low" => profileData.TargetLow,
            "target_high" => profileData.TargetHigh,
            "basal" => profileData.Basal,
            _ => null,
        };
    }

    private double GetValueFromContainer(
        object? valueContainer,
        int timeAsSecondsFromMidnight,
        string valueType
    )
    {
        if (valueContainer == null)
            return GetDefaultValue(valueType);

        // If it's a simple value (like dia, carbs_hr)
        if (valueContainer is double doubleValue)
        {
            return doubleValue;
        }

        if (valueContainer is int intValue)
        {
            return intValue;
        }

        // If it's a time-based array
        if (valueContainer is List<TimeValue> timeValues && timeValues.Any())
        {
            // Sort by time to ensure we have the correct order
            var sortedValues = timeValues.OrderBy(tv => tv.TimeAsSeconds ?? 0).ToList();
            var returnValue = sortedValues[0].Value; // Default to first (earliest) value

            // Find the most recent time slot before or at the current time
            foreach (var timeValue in sortedValues)
            {
                if (timeAsSecondsFromMidnight >= (timeValue.TimeAsSeconds ?? 0))
                {
                    returnValue = timeValue.Value;
                }
                else
                {
                    // We've gone past the current time, use the previous value
                    break;
                }
            }

            return returnValue;
        }

        return GetDefaultValue(valueType);
    }

    private double GetDefaultValue(string valueType)
    {
        return valueType switch
        {
            "dia" => 3.0,
            "sens" => 50.0,
            "carbratio" => 12.0,
            "carbs_hr" => 20.0,
            "target_low" => 70.0,
            "target_high" => 180.0,
            "basal" => 1.0,
            _ => 0.0,
        };
    }

    private Profile CreateProfileFromData(ProfileData? data)
    {
        if (data == null)
            return new Profile();

        return new Profile
        {
            Store = new Dictionary<string, ProfileData> { { "Default", data } },
            DefaultProfile = "Default",
        };
    }

    private void HandleProfileJson(Treatment treatment, Profile? pdataActive)
    {
        if (!string.IsNullOrEmpty(treatment.ProfileJson) && pdataActive != null)
        {
            var profileName = treatment.Profile ?? "";

            if (!profileName.Contains("@@@@@"))
            {
                profileName += $"@@@@@{treatment.Mills}";
                treatment.Profile = profileName;
            }

            if (!pdataActive.Store.ContainsKey(profileName))
            {
                // Parse JSON and add to store
                try
                {
                    var profileData = JsonSerializer.Deserialize<ProfileData>(
                        treatment.ProfileJson
                    );
                    if (profileData != null)
                    {
                        pdataActive.Store[profileName] = profileData;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "Failed to parse profile JSON for treatment {TreatmentId}",
                        treatment.Id
                    );
                }
            }
        }
    }
}

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Unified profile interface for both COB and IOB calculations with 1:1 legacy compatibility
/// Must match profile functions from ClientApp/lib/profilefunctions.js
/// </summary>
public interface IProfileService
{
    // Core profile data management
    void LoadData(List<Profile> profileData);
    bool HasData();
    void Clear();

    // Profile retrieval and selection
    Profile? GetCurrentProfile(long? time = null, string? specProfile = null);
    string? GetActiveProfileName(long? time = null);
    List<string> ListBasalProfiles();
    string? GetUnits(string? specProfile = null);
    string? GetTimezone(string? specProfile = null);

    // Time-based value retrieval (core legacy functionality)
    double GetValueByTime(long time, string valueType, string? specProfile = null);

    // Specific profile values (for COB/IOB calculations)
    double GetDIA(long time, string? specProfile = null);
    double GetSensitivity(long time, string? specProfile = null);
    double GetCarbRatio(long time, string? specProfile = null);
    double GetCarbAbsorptionRate(long time, string? specProfile = null);
    double GetLowBGTarget(long time, string? specProfile = null);
    double GetHighBGTarget(long time, string? specProfile = null);
    double GetBasalRate(long time, string? specProfile = null);

    // Treatment integration
    void UpdateTreatments(
        List<Treatment>? profileTreatments = null,
        List<Treatment>? tempBasalTreatments = null,
        List<Treatment>? comboBolusTreatments = null
    );
    Treatment? GetActiveProfileTreatment(long time);
    Treatment? GetTempBasalTreatment(long time);
    Treatment? GetComboBolusTreatment(long time);
    TempBasalResult GetTempBasal(long time, string? specProfile = null);
}

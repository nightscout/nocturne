namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Resolves scalar therapy settings (DIA, carb absorption rate, timezone, units)
/// from the active profile's TherapySettings record.
/// </summary>
public interface ITherapySettingsResolver
{
    /// <summary>
    /// Returns the Duration of Insulin Action in hours. Priority: ExternallyManaged DIA,
    /// then PatientInsulin primary bolus DIA, then TherapySettings DIA, then default (3.0).
    /// </summary>
    Task<double> GetDIAAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the carb absorption rate in grams per hour. Defaults to 20.0.
    /// </summary>
    Task<double> GetCarbAbsorptionRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the IANA timezone for the profile, or null if not set.
    /// </summary>
    Task<string?> GetTimezoneAsync(string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the blood glucose units ("mg/dL" or "mmol/L") for the profile, or null if not set.
    /// </summary>
    Task<string?> GetUnitsAsync(string? specProfile = null, CancellationToken ct = default);

    /// <summary>
    /// Returns true if any TherapySettings record exists for the current tenant.
    /// </summary>
    Task<bool> HasDataAsync(CancellationToken ct = default);
}

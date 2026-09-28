using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// The glucose units and timezone onboarding asks the owner to confirm, and the stores one answer
/// seeds.
/// </summary>
/// <remarks>
/// One units answer sets the owner's own display units and the tenant default
/// (<see cref="DisplaySettings.Units"/>), which a member who has never chosen their own reads
/// until they do. Therapy settings are entered in the editor's display units, so the owner's
/// display units are also the unit the owner enters them in; a profile's stored
/// <c>TherapySettings.Units</c> is never rewritten, because it says what its numbers mean. The
/// timezone is the patient's (<c>PatientRecord.Timezone</c>).
/// </remarks>
public interface IUnitsAndTimezoneService
{
    /// <summary>
    /// The answer to pre-fill. Units come from the Nightscout instance's display units when
    /// <paramref name="readNightscout"/> reads them, then from the owner's saved display units,
    /// then from <paramref name="locale"/>. The timezone comes from the Nightscout profile, then
    /// from the patient record; null leaves it to the browser.
    /// </summary>
    Task<UnitsAndTimezoneDto> GetAsync(
        Guid ownerSubjectId, string? locale, bool readNightscout, CancellationToken ct = default);

    /// <summary>
    /// A member's own display preferences, with the tenant default units in place of units the
    /// member has never chosen. The stored preferences are left as they are, so the member's
    /// own later choice still wins.
    /// </summary>
    Task<UserDisplayPreferences> WithTenantDefaultsAsync(UserDisplayPreferences own, CancellationToken ct = default);

    /// <summary>Saves the answer. <paramref name="glucoseUnits"/> is "mg/dl" or "mmol".</summary>
    /// <exception cref="InvalidOperationException">The tenant's display settings could not be read.</exception>
    Task<UnitsAndTimezoneDto> SetAsync(
        Guid ownerSubjectId, string glucoseUnits, string timezone, CancellationToken ct = default);
}

/// <param name="GlucoseUnits">"mg/dl" or "mmol".</param>
/// <param name="Timezone">IANA timezone, or null when none is known yet.</param>
/// <param name="Nightscout">What the saved Nightscout instance says, when it was asked and answered.</param>
/// <param name="NightscoutUnavailable">A saved Nightscout instance was asked and could not be read.</param>
public record UnitsAndTimezoneDto(
    string GlucoseUnits,
    string? Timezone,
    NightscoutDisplaySettings? Nightscout = null,
    bool NightscoutUnavailable = false);

/// <inheritdoc />
public class UnitsAndTimezoneService(
    NocturneDbContext db,
    IUISettingsService uiSettings,
    IPatientRecordRepository patientRecords,
    IConnectorConfigurationService connectorConfigurations,
    IMigrationJobService migrations,
    ILogger<UnitsAndTimezoneService> logger) : IUnitsAndTimezoneService
{
    /// <summary>The connector onboarding saves a Nightscout instance under.</summary>
    public const string NightscoutConnector = "nightscout";

    public async Task<UnitsAndTimezoneDto> GetAsync(
        Guid ownerSubjectId, string? locale, bool readNightscout, CancellationToken ct = default)
    {
        var (nightscout, unavailable) = readNightscout ? await ReadNightscoutAsync(ct) : (null, false);

        var preferences = await db.Subjects.AsNoTracking()
            .Where(s => s.Id == ownerSubjectId)
            .Select(s => s.Preferences)
            .FirstOrDefaultAsync(ct);
        var record = await patientRecords.GetAsync(ct);

        return new UnitsAndTimezoneDto(
            nightscout?.DisplayUnits
                ?? UserDisplayPreferences.Deserialize(preferences).GlucoseUnits
                ?? GlucoseUnitDefaults.ForLocale(locale),
            nightscout?.ProfileTimezone ?? record?.Timezone,
            nightscout,
            unavailable);
    }

    public async Task<UserDisplayPreferences> WithTenantDefaultsAsync(
        UserDisplayPreferences own, CancellationToken ct = default)
    {
        if (own.GlucoseUnits is null
            && await uiSettings.GetSectionAsync<FeatureSettings>(UISettingsSections.Features, ct) is { } features)
        {
            own.GlucoseUnits = GlucoseUnitDefaults.Normalize(features.Display.Units);
        }

        return own;
    }

    public async Task<UnitsAndTimezoneDto> SetAsync(
        Guid ownerSubjectId, string glucoseUnits, string timezone, CancellationToken ct = default)
    {
        var features = await uiSettings.GetSectionAsync<FeatureSettings>(UISettingsSections.Features, ct)
            ?? throw new InvalidOperationException("The tenant's display settings could not be read.");
        var subject = await db.Subjects.FirstAsync(s => s.Id == ownerSubjectId, ct);

        var preferences = UserDisplayPreferences.Deserialize(subject.Preferences);
        preferences.GlucoseUnits = glucoseUnits;
        subject.Preferences = preferences.Serialize();
        subject.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        features.Display.Units = glucoseUnits;
        await uiSettings.SaveSectionAsync(UISettingsSections.Features, features, ct);

        var record = await patientRecords.GetOrCreateAsync(ct);
        record.Timezone = timezone;
        await patientRecords.UpdateAsync(record, WriteOrigin.Live, ct);

        return new UnitsAndTimezoneDto(glucoseUnits, timezone);
    }

    private async Task<(NightscoutDisplaySettings?, bool Unavailable)> ReadNightscoutAsync(CancellationToken ct)
    {
        var source = await NightscoutConnectorSource.ReadAsync(connectorConfigurations, NightscoutConnector, ct);
        if (string.IsNullOrEmpty(source?.Url))
            return (null, false);

        try
        {
            return (await migrations.ReadDisplaySettingsAsync(source.Url, source.ApiSecret, ct), false);
        }
        catch (Exception ex) when (ex is MigrationSourceException or JsonException)
        {
            logger.LogWarning(ex, "Could not read the saved Nightscout instance's display settings");
            return (null, true);
        }
    }
}

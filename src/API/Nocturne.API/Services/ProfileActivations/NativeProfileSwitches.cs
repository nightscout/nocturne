using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>What a write to a native profile switch came to.</summary>
public enum NativeProfileSwitchOutcome
{
    Written,
    NotFound,
    ProfileNotFound,
    PatientDeviceNotFound,

    /// <summary>The activation was written by an uploader or a connector, which own it.</summary>
    NotOwnedByUser,
    AlreadyEnded,
}

/// <summary>
/// The profile switches a person records in Nocturne: requested activations of a stored profile, with
/// the profile's schedules as they stand captured as the base snapshot
/// (<see cref="ProfileActivationSource.IsNative"/>).
/// </summary>
/// <remarks>
/// Record-only: they carry no legacy id, so <see cref="LegacyProfileActivationTable"/> never serves them
/// on the v1 or v3 treatments feed. AAPS imports every Profile Switch it reads there, an invalid one
/// included (<c>SyncNsProfileSwitchTransaction</c> inserts an unknown one whatever its <c>isValid</c>),
/// so leaving them off the feed is what keeps the pump from running them.
/// </remarks>
public sealed class NativeProfileSwitches
{
    private readonly IProfileActivationRepository _activations;
    private readonly ProfileActivationWriter _writer;
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly IBasalScheduleRepository _basal;
    private readonly ICarbRatioScheduleRepository _carbRatio;
    private readonly ISensitivityScheduleRepository _sensitivity;
    private readonly ITargetRangeScheduleRepository _targets;
    private readonly IPatientDeviceRepository _patientDevices;
    private readonly TimeProvider _time;

    public NativeProfileSwitches(
        IProfileActivationRepository activations,
        ProfileActivationWriter writer,
        ITherapySettingsRepository therapySettings,
        IBasalScheduleRepository basal,
        ICarbRatioScheduleRepository carbRatio,
        ISensitivityScheduleRepository sensitivity,
        ITargetRangeScheduleRepository targets,
        IPatientDeviceRepository patientDevices,
        TimeProvider time)
    {
        _activations = activations;
        _writer = writer;
        _therapySettings = therapySettings;
        _basal = basal;
        _carbRatio = carbRatio;
        _sensitivity = sensitivity;
        _targets = targets;
        _patientDevices = patientDevices;
        _time = time;
    }

    public async Task<(NativeProfileSwitchOutcome Outcome, ProfileActivation? Activation)> CreateAsync(
        CreateProfileActivationRequest request, CancellationToken ct)
    {
        var now = Now();
        var name = request.BaseProfileName;
        if (name.Contains(TherapySettings.ProfileSwitchStoreMarker, StringComparison.Ordinal)
            || await _therapySettings.GetActiveAtAsync(name, now, ct) is not { } settings)
            return (NativeProfileSwitchOutcome.ProfileNotFound, null);

        if (request.PatientDeviceId is { } deviceId && await _patientDevices.GetByIdAsync(deviceId, ct) is null)
            return (NativeProfileSwitchOutcome.PatientDeviceNotFound, null);

        var activation = new ProfileActivation
        {
            StartTimestamp = now,
            Phase = ProfileActivationPhase.Requested,
            Origin = ProfileActivationOrigin.User,
            DataSource = DataSources.ManualEntry,
            BaseProfileName = name,
            Percentage = request.Percentage,
            TimeshiftMs = request.TimeshiftHours * 3_600_000L,
            RequestedDurationMs = request.DurationMinutes is > 0 and var minutes ? minutes * 60_000L : null,
            Snapshot = new ProfileActivationSnapshot
            {
                Basal = (await _basal.GetActiveAtAsync(name, now, ct))?.Entries,
                CarbRatio = (await _carbRatio.GetActiveAtAsync(name, now, ct))?.Entries,
                Sensitivity = (await _sensitivity.GetActiveAtAsync(name, now, ct))?.Entries,
                Targets = (await _targets.GetActiveAtAsync(name, now, ct))?.Entries,
                Units = settings.Units,
                Dia = settings.Dia,
                CarbsHr = settings.CarbsHr,
                Timezone = settings.Timezone,
            },
            SnapshotIsCustomized = false,
            PatientDeviceId = request.PatientDeviceId,
            AdditionalProperties = string.IsNullOrWhiteSpace(request.Notes)
                ? null
                : new Dictionary<string, object?> { [LegacyTreatmentTables.NotesKey] = request.Notes },
        };

        var written = await _writer.WriteAsync(activation, null,
            async settled => await _activations.CreateAsync(settled, WriteOrigin.Live, ct), ct);
        return (NativeProfileSwitchOutcome.Written, written);
    }

    /// <summary>Ends a person's activation now, as <see cref="ProfileActivationWriter.EndAsync"/> does.</summary>
    public async Task<(NativeProfileSwitchOutcome Outcome, ProfileActivation? Activation)> EndAsync(
        Guid id, CancellationToken ct)
    {
        var now = Now();
        if (await _activations.GetByIdAsync(id, ct) is not { } activation)
            return (NativeProfileSwitchOutcome.NotFound, null);
        if (activation.Origin != ProfileActivationOrigin.User)
            return (NativeProfileSwitchOutcome.NotOwnedByUser, null);
        if (ProfileActivationSelection.EndOf(activation) is { } end && end <= ToMills(now))
            return (NativeProfileSwitchOutcome.AlreadyEnded, null);

        var written = await _writer.EndAsync(activation, now,
            async settled => await _activations.UpdateAsync(settled.Id, settled, WriteOrigin.Live, ct), ct);
        return (NativeProfileSwitchOutcome.Written, written);
    }

    /// <summary>Soft-deletes a person's activation and settles again the neighbours it ended.</summary>
    public async Task<NativeProfileSwitchOutcome> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await _activations.GetByIdAsync(id, ct) is not { } activation)
            return NativeProfileSwitchOutcome.NotFound;
        if (activation.Origin != ProfileActivationOrigin.User)
            return NativeProfileSwitchOutcome.NotOwnedByUser;

        await _activations.DeleteAsync(id, WriteOrigin.Live, ct);
        await _writer.ResettleAfterRemovalAsync([id], ct);
        return NativeProfileSwitchOutcome.Written;
    }

    /// <summary>
    /// Now, to the millisecond: the writer matches an end to a neighbour's start exactly, and the stored
    /// timestamp keeps microseconds, not ticks.
    /// </summary>
    private DateTime Now() =>
        DateTimeOffset.FromUnixTimeMilliseconds(_time.GetUtcNow().ToUnixTimeMilliseconds()).UtcDateTime;

    private static long ToMills(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();
}

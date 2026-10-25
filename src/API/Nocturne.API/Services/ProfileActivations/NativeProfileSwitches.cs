using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>What a write to a switch recorded in Nocturne came to.</summary>
public enum NativeProfileSwitchOutcome
{
    Written,
    NotFound,
    ProfileNotFound,

    /// <summary>The activation was uploaded as a treatment and is served back to its uploader, which owns it.</summary>
    Uploaded,

    /// <summary>The activation was reported by a connector or an uploader, which owns it.</summary>
    OwnedBySource,
    AlreadyEnded,
}

public sealed record NativeProfileSwitchResult(NativeProfileSwitchOutcome Outcome, ProfileActivation? Activation = null);

/// <summary>
/// The profile switches a person records in Nocturne: requested activations of a stored profile, with
/// the profile's schedules as they stand captured as the base snapshot
/// (<see cref="ProfileActivationSource.IsNative"/>), which they keep running when the profile is edited,
/// as an AAPS profile switch does.
/// </summary>
/// <remarks>
/// Record-only: they carry no legacy id, so <see cref="LegacyProfileActivationTable"/> never serves them
/// on the v1 or v3 treatments feed, and no follower or uploader ever reads them. AAPS imports every
/// Profile Switch it reads there, an invalid one included (<c>SyncNsProfileSwitchTransaction</c> inserts
/// an unknown one whatever its <c>isValid</c>, and overwrites the <c>isValid</c> of a local switch at the
/// same time), so leaving them off the feed is what keeps the pump from running them. While an AID
/// system reports what its pump runs, they do not apply (<see cref="ProfileActivationSelection"/>).
/// </remarks>
public sealed class NativeProfileSwitches
{
    private const int Neighbourhood = 200;

    private readonly IProfileActivationRepository _activations;
    private readonly ProfileActivationWriter _writer;
    private readonly IProfileActivationResolver _resolver;
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly IBasalScheduleRepository _basal;
    private readonly ICarbRatioScheduleRepository _carbRatio;
    private readonly ISensitivityScheduleRepository _sensitivity;
    private readonly ITargetRangeScheduleRepository _targets;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _time;

    public NativeProfileSwitches(
        IProfileActivationRepository activations,
        ProfileActivationWriter writer,
        IProfileActivationResolver resolver,
        ITherapySettingsRepository therapySettings,
        IBasalScheduleRepository basal,
        ICarbRatioScheduleRepository carbRatio,
        ISensitivityScheduleRepository sensitivity,
        ITargetRangeScheduleRepository targets,
        IAuditContext audit,
        TimeProvider time)
    {
        _activations = activations;
        _writer = writer;
        _resolver = resolver;
        _therapySettings = therapySettings;
        _basal = basal;
        _carbRatio = carbRatio;
        _sensitivity = sensitivity;
        _targets = targets;
        _audit = audit;
        _time = time;
    }

    public Task<NativeProfileSwitchResult> CreateAsync(CreateProfileActivationRequest request, CancellationToken ct) =>
        RecordAsync(
            request.BaseProfileName,
            request.Percentage,
            request.TimeshiftHours * 3_600_000L,
            request.DurationMinutes is > 0 and var minutes ? minutes * 60_000L : null,
            request.Notes,
            ct);

    /// <summary>
    /// Records <paramref name="id"/> again with its profile's current schedules: the same percentage and
    /// shift, permanent if it was, else until the end it already had.
    /// </summary>
    public async Task<NativeProfileSwitchResult> RenewAsync(Guid id, CancellationToken ct)
    {
        var now = Now();
        var (activation, refusal) = await OwnedAsync(id, ct);
        if (activation is null)
            return new(refusal);
        if (ProfileActivationSelection.EndOf(activation) is { } end && end <= now.ToMills())
            return new(NativeProfileSwitchOutcome.AlreadyEnded);

        return await RecordAsync(
            activation.BaseProfileName,
            activation.Percentage,
            activation.TimeshiftMs,
            ProfileActivationSelection.EndOf(activation) is { } until ? until - now.ToMills() : null,
            activation.AdditionalProperties?.GetValueOrDefault(ProfileActivation.NotesKey)?.ToString(),
            ct);
    }

    /// <summary>Ends a recorded switch now, as <see cref="ProfileActivationWriter.EndAsync"/> does.</summary>
    public async Task<NativeProfileSwitchResult> EndAsync(Guid id, CancellationToken ct)
    {
        var now = Now();
        var (activation, refusal) = await OwnedAsync(id, ct);
        if (activation is null)
            return new(refusal);
        if (ProfileActivationSelection.EndOf(activation) is { } end && end <= now.ToMills())
            return new(NativeProfileSwitchOutcome.AlreadyEnded);

        var written = await _writer.EndAsync(activation, now, _audit.SubjectName,
            async settled => await _activations.UpdateAsync(settled.Id, settled, WriteOrigin.Live, ct), ct);
        return new(NativeProfileSwitchOutcome.Written, written);
    }

    /// <summary>Soft-deletes a recorded switch and settles again the neighbours it ended.</summary>
    public async Task<NativeProfileSwitchOutcome> DeleteAsync(Guid id, CancellationToken ct)
    {
        var (activation, refusal) = await OwnedAsync(id, ct);
        if (activation is null)
            return refusal;

        await _activations.DeleteAsync(id, WriteOrigin.Live, ct);
        await _writer.ResettleAfterRemovalAsync([id], ct);
        return NativeProfileSwitchOutcome.Written;
    }

    public async Task<ProfileActivationStatus> GetStatusAsync(CancellationToken ct)
    {
        var now = Now();
        var nowMills = now.ToMills();
        var running = await _activations.GetOverlappingAsync(now, now, Neighbourhood, ct);
        var reported = ProfileActivationSelection.ReportedAt(running, nowMills);
        var recorded = ProfileActivationSelection.NativeAt(running, nowMills);
        var active = await _resolver.GetAtAsync(nowMills, ct);

        var changed = recorded?.Snapshot is { } snapshot
            && await CaptureAsync(recorded.BaseProfileName, now, ct) is { } current
            && ProfileActivationSnapshots.SchedulesDiffer(snapshot, current);

        return new ProfileActivationStatus
        {
            Active = active is null ? null : ProfileActivationDto.From(active.Activation),
            Reported = reported is null ? null : ProfileActivationDto.From(reported),
            Recorded = recorded is null ? null : ProfileActivationDto.From(recorded),
            RecordedSwitchesApply = reported is null,
            RecordedProfileChanged = changed,
        };
    }

    private async Task<NativeProfileSwitchResult> RecordAsync(
        string name, int percentage, long timeshiftMs, long? durationMs, string? notes, CancellationToken ct)
    {
        var now = Now();
        if (await CaptureAsync(name, now, ct) is not { } snapshot)
            return new(NativeProfileSwitchOutcome.ProfileNotFound);

        var activation = new ProfileActivation
        {
            StartTimestamp = await StartAsync(now, ct),
            Phase = ProfileActivationPhase.Requested,
            Origin = ProfileActivationOrigin.User,
            DataSource = DataSources.ManualEntry,
            BaseProfileName = name,
            Percentage = percentage,
            TimeshiftMs = timeshiftMs,
            RequestedDurationMs = durationMs is > 0 ? durationMs : null,
            Snapshot = snapshot,
            SnapshotIsCustomized = false,
            EnteredBy = _audit.SubjectName,
            AdditionalProperties = string.IsNullOrWhiteSpace(notes)
                ? null
                : new Dictionary<string, object?> { [ProfileActivation.NotesKey] = notes },
        };

        var written = await _writer.WriteAsync(activation, null,
            async settled => await _activations.CreateAsync(settled, WriteOrigin.Live, ct), ct);
        return new(NativeProfileSwitchOutcome.Written, written);
    }

    /// <summary>
    /// <paramref name="now"/>, or a millisecond after the newest recorded switch when that started at the
    /// same millisecond: a second submit then supersedes the first rather than tying with it.
    /// </summary>
    private async Task<DateTime> StartAsync(DateTime now, CancellationToken ct)
    {
        var latest = (await _activations.GetOverlappingAsync(now, now, Neighbourhood, ct))
            .Where(ProfileActivationSource.IsNative)
            .Select(a => a.StartTimestamp)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        return latest >= now ? latest.AddMilliseconds(1) : now;
    }

    /// <summary>The stored profile's schedules at <paramref name="at"/>; null when the store has no such profile.</summary>
    private async Task<ProfileActivationSnapshot?> CaptureAsync(string name, DateTime at, CancellationToken ct)
    {
        if (await _therapySettings.GetActiveAtAsync(name, at, ct) is not { } settings)
            return null;

        return new ProfileActivationSnapshot
        {
            Basal = (await _basal.GetActiveAtAsync(name, at, ct))?.Entries,
            CarbRatio = (await _carbRatio.GetActiveAtAsync(name, at, ct))?.Entries,
            Sensitivity = (await _sensitivity.GetActiveAtAsync(name, at, ct))?.Entries,
            Targets = (await _targets.GetActiveAtAsync(name, at, ct))?.Entries,
            Units = settings.Units,
            Dia = settings.Dia,
            CarbsHr = settings.CarbsHr,
            Timezone = settings.Timezone,
        };
    }

    /// <summary>The live activation <paramref name="id"/> when it is a recorded switch, else why it may not be changed here.</summary>
    private async Task<(ProfileActivation? Activation, NativeProfileSwitchOutcome Refusal)> OwnedAsync(
        Guid id, CancellationToken ct) =>
        await _activations.GetByIdAsync(id, ct) switch
        {
            null => (null, NativeProfileSwitchOutcome.NotFound),
            { LegacyId: not null } => (null, NativeProfileSwitchOutcome.Uploaded),
            var a when !ProfileActivationSource.IsNative(a) => (null, NativeProfileSwitchOutcome.OwnedBySource),
            var a => (a, NativeProfileSwitchOutcome.Written),
        };

    /// <summary>
    /// Now, to the millisecond: the writer matches an end to a neighbour's start exactly, and the stored
    /// timestamp keeps microseconds, not ticks.
    /// </summary>
    private DateTime Now() =>
        DateTimeOffset.FromUnixTimeMilliseconds(_time.GetUtcNow().ToUnixTimeMilliseconds()).UtcDateTime;
}

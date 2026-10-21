using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// The active profile at a time, read from the <see cref="IProfileActivationResolver"/>.
/// </summary>
internal sealed class ActiveProfileResolver : IActiveProfileResolver
{
    /// <summary>The store read when no row is the tenant's default.</summary>
    private const string ImplicitDefaultProfileName = "Default";

    private readonly IProfileActivationResolver _activations;
    private readonly IProfileActivationRepository _repository;
    private readonly ITherapySettingsRepository _therapySettings;

    private string? _defaultProfileName;

    public ActiveProfileResolver(
        IProfileActivationResolver activations,
        IProfileActivationRepository repository,
        ITherapySettingsRepository therapySettings)
    {
        _activations = activations;
        _repository = repository;
        _therapySettings = therapySettings;
    }

    public async Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default) =>
        (await _activations.GetAtAsync(timeMills, ct))?.ProfileName;

    /// <remarks>
    /// The <see cref="TherapySettings.IsDefault"/> row is the store the newest profile document names
    /// as its <c>defaultProfile</c>, which Nightscout reads as <c>store[defaultProfile]</c>. Read once
    /// per scope: no cache family is cleared when a profile write moves the default.
    /// </remarks>
    public async Task<string> GetDefaultProfileNameAsync(CancellationToken ct = default) =>
        _defaultProfileName ??= (await _therapySettings.GetDefaultsAsync(ct)).FirstOrDefault()?.ProfileName
            ?? ImplicitDefaultProfileName;

    public async Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default) =>
        (await _activations.GetAtAsync(timeMills, ct))?.Scaling;

    public async Task<ProfileActivationSnapshot?> GetActiveSchedulesAsync(long timeMills, CancellationToken ct = default) =>
        (await _activations.GetAtAsync(timeMills, ct))?.Schedules;

    public async Task<IReadOnlyList<ProfileSpan>> GetActiveProfileSpansForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default) =>
        (await _activations.GetRangeAsync(fromMs, toMs, ct))
            .Select(s => new ProfileSpan(s.Active.ProfileName, s.StartMills, s.EndMills, s.Active.Scaling, s.Active.Schedules))
            .ToList();

    /// <remarks>
    /// An effective activation uploaded without the insulin falls back to the requested activation
    /// it confirms, which an uploader may have sent it on instead.
    /// </remarks>
    public async Task<TreatmentInsulinContext?> GetActiveInsulinContextAsync(long timeMills, CancellationToken ct = default)
    {
        if ((await _activations.GetAtAsync(timeMills, ct))?.Activation is not { } active)
            return null;

        if (active.Snapshot?.Insulin is { } insulin)
            return insulin;

        return active.RequestedActivationId is { } requestedId
            ? (await _repository.GetByIdAsync(requestedId, ct))?.Snapshot?.Insulin
            : null;
    }
}

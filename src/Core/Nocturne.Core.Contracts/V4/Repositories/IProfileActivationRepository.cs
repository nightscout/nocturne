using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>Repository for <see cref="ProfileActivation"/> records.</summary>
public interface IProfileActivationRepository
    : ILegacyKeyedRepository<ProfileActivation>,
      ISyncKeyedRepository<ProfileActivation>,
      IDeviceAttributedRepository<ProfileActivation>
{
    /// <summary>
    /// The live activations that overlap <paramref name="from"/>..<paramref name="to"/>, by start, oldest
    /// first: started at or before <paramref name="to"/>, and open or ended after <paramref name="from"/>.
    /// </summary>
    Task<IReadOnlyList<ProfileActivation>> GetOverlappingAsync(
        DateTime from, DateTime to, int limit, CancellationToken ct = default);

    /// <summary>The live activations that start after <paramref name="after"/>, oldest first.</summary>
    Task<IReadOnlyList<ProfileActivation>> GetStartingAfterAsync(
        DateTime after, int limit, CancellationToken ct = default);

    /// <summary>The live requested activations started at or before <paramref name="at"/>, newest first.</summary>
    Task<IReadOnlyList<ProfileActivation>> GetRequestedAtOrBeforeAsync(
        DateTime at, int limit, CancellationToken ct = default);

    /// <summary>
    /// The data source, app and uploader of every live effective activation, one row per distinct
    /// combination, with the earliest one's start: the sources whose requested activations from then on
    /// never resolve on their own.
    /// </summary>
    Task<IReadOnlyList<(string? DataSource, string? App, string? EnteredBy, DateTime FirstStart)>> GetEffectiveSourcesAsync(
        CancellationToken ct = default);

    /// <summary>The activations with <paramref name="ids"/>, deleted or not.</summary>
    Task<IReadOnlyList<ProfileActivation>> GetIncludingDeletedAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// The live activations that end at one of <paramref name="starts"/> or confirm one of
    /// <paramref name="ids"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<ProfileActivation>> GetSettledByAsync(
        IReadOnlyCollection<DateTime> starts, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>The live activation <paramref name="dataSource"/> wrote under <paramref name="syncIdentifier"/>.</summary>
    Task<ProfileActivation?> FindBySyncIdentifierAsync(
        string dataSource, string syncIdentifier, CancellationToken ct = default);

    /// <summary>Writes a set of activations' ends in one save; ids with no live row are skipped.</summary>
    Task<int> SetEndsAsync(IReadOnlyDictionary<Guid, DateTime?> ends, CancellationToken ct = default);

    /// <summary>Links effective activations to the requested activation they confirm, or to none.</summary>
    Task<int> SetRequestedActivationAsync(
        IReadOnlyCollection<Guid> effectiveIds, Guid? requestedId, CancellationToken ct = default);
}

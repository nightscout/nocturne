namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// The upstream fingerprints the writes under way belong to, keyed by data source and legacy id.
/// While a scope is open, every save of an <see cref="IUpstreamFingerprinted"/> row whose source and
/// legacy id it names writes that fingerprint in the same statement, null included. A row the scope
/// does not name keeps its fingerprint.
/// </summary>
/// <remarks>
/// Ambient rather than passed, because the rows are written by the repositories a decomposer calls,
/// each on a context of its own. Two rules open one. A connector publish names the fingerprint of
/// each treatment it hands over. A treatment decomposed with no scope open, which is another uploader
/// restating the record through the v1 API, names null: the connector then takes the record as it
/// stands as its new baseline rather than overwriting it. Every other write (a v4 edit, a sweep)
/// opens none, so an edit made in Nocturne keeps the fingerprint and survives until the source
/// changes the record. Work started inside a scope through <c>Task.Run</c> or left running after it
/// closes captures the scope too, so nothing that writes these rows may be launched that way.
/// </remarks>
public static class UpstreamFingerprintScope
{
    private static readonly AsyncLocal<IReadOnlyDictionary<(string? Source, string LegacyId), string?>?> Current = new();

    public static bool IsOpen => Current.Value is not null;

    public static IDisposable Open(IReadOnlyDictionary<(string? Source, string LegacyId), string?> fingerprints)
    {
        var prior = Current.Value;
        Current.Value = fingerprints;
        return new Closer(prior);
    }

    internal static bool TryGet(string? source, string legacyId, out string? fingerprint)
    {
        fingerprint = null;
        return Current.Value?.TryGetValue((source, legacyId), out fingerprint) == true;
    }

    private sealed class Closer(IReadOnlyDictionary<(string? Source, string LegacyId), string?>? prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}

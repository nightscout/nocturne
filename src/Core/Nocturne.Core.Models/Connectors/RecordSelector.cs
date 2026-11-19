namespace Nocturne.Core.Models.Connectors;

/// <summary>
/// The records a delete or a re-import release selects: those a source produced, by either origin
/// handle, or those a set of credentials inserted.
/// </summary>
/// <remarks>
/// Two credentials can name the same handle, so only <see cref="ByWriters"/> selects one
/// credential's records apart from another's.
/// </remarks>
public abstract record RecordSelector
{
    private RecordSelector()
    {
    }

    /// <summary>The scope the delete's bulk audit summary is recorded under.</summary>
    public abstract string Scope { get; }

    public static RecordSelector ForSource(string source) => new BySource(source);

    public static RecordSelector ForWriters(IReadOnlyCollection<Guid> grantIds) =>
        grantIds.Count == 0
            ? throw new ArgumentException("A writer selector needs at least one grant id.", nameof(grantIds))
            : new ByWriters([.. grantIds.Distinct().Order()]);

    /// <summary>The rows whose <c>data_source</c> or <c>device</c> is <paramref name="Source"/>.</summary>
    public sealed record BySource(string Source) : RecordSelector
    {
        public override string Scope => $"data_source={Source}";
    }

    /// <summary>The rows whose <c>writer_grant_id</c> is one of <paramref name="GrantIds"/>.</summary>
    public sealed record ByWriters(IReadOnlyList<Guid> GrantIds) : RecordSelector
    {
        public override string Scope => $"writer_grant_id={string.Join(',', GrantIds)}";
    }
}

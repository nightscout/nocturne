namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Narrows the memberships that still give access a reason no longer gives, because that reason has
/// ended, to the sum of the reasons still in force.
/// </summary>
/// <remarks>
/// Such a membership is absent to every access read from the moment its reason ends; the reads that
/// decide a request or a sign-in narrow it themselves, and this catches every other one.
/// </remarks>
public interface IMembershipReasonLapses
{
    /// <summary>
    /// Narrows every membership whose reason has ended at <paramref name="now"/>, each in its own
    /// transaction. One that fails is logged and the others are still narrowed.
    /// </summary>
    Task<MembershipLapseSweep> NarrowLapsedAsync(DateTime now, CancellationToken ct);
}

/// <summary>What <see cref="IMembershipReasonLapses.NarrowLapsedAsync"/> did.</summary>
/// <param name="Recomputed">Memberships narrowed, or found already narrowed.</param>
/// <param name="Failed">Memberships that could not be narrowed, and wait for the next run.</param>
public sealed record MembershipLapseSweep(int Recomputed, int Failed);

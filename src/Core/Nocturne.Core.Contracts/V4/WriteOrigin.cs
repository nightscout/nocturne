namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Classifies why a V4 write is happening, so the repository chokepoint can decide whether the write
/// is a genuinely-new live event worth broadcasting or a bulk/historical import that must stay silent.
/// </summary>
/// <remarks>
/// Required (no default) on every mutating repository method and on the decomposers. Ambient/AsyncLocal
/// scoping was rejected: pooled DbContext carriers in this codebase have leaked prior-lessee state, so
/// the origin is threaded explicitly as a parameter instead.
/// </remarks>
public enum WriteOrigin
{
    /// <summary>
    /// A genuinely-new write from a live path — v1/v3 uploads, native V4 controller writes, or a
    /// connector's incremental catch-up. The chokepoint broadcasts these.
    /// </summary>
    Live,

    /// <summary>
    /// A bulk/historical import — MongoDB migration or a connector's initial full-history sync. The
    /// chokepoint suppresses broadcasts so clients aren't flooded with data that isn't new.
    /// </summary>
    Backfill,
}

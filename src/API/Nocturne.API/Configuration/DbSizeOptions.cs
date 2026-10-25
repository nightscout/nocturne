namespace Nocturne.API.Configuration;

/// <summary>
/// The size a tenant's store is measured against by the <c>dbsize</c> property. Bound to the
/// <c>DbSize</c> configuration section.
/// </summary>
public sealed class DbSizeOptions
{
    public const string SectionName = "DbSize";

    /// <summary>Budget for one tenant's data and indexes, in MiB.</summary>
    public int MaxMiB { get; set; } = 2048;

    public long MaxBytes => (long)Math.Max(MaxMiB, 1) * 1024 * 1024;
}

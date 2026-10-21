namespace Nocturne.Core.Models.V4;

/// <summary>
/// Shared implementation of the <see cref="IV4Record"/> header carried by every V4 record type.
/// </summary>
/// <remarks>
/// <inheritdoc cref="IV4Record" path="/remarks"/>
/// <para>
/// V4 records are API response types whose schemas are flat on the wire, so every derived record
/// carries <c>[JsonSchemaFlatten]</c> — NSwag then inlines these members into that record's schema
/// rather than emitting an <c>allOf</c> reference to a base schema. NJsonSchema reads the attribute
/// off the type being generated and does not inherit it, so it cannot be declared here once.
/// </para>
/// </remarks>
public abstract class V4RecordBase : IV4Record
{
    /// <inheritdoc />
    public Guid Id { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Always UTC once set: a body timestamp with an offset binds as <see cref="DateTimeKind.Local"/>,
    /// which <see cref="Mills"/> and the database both reject on a host off UTC.
    /// </remarks>
    public DateTime Timestamp
    {
        get => _timestamp;
        set => _timestamp = value.AsUtc();
    }

    private DateTime _timestamp;

    /// <inheritdoc />
    /// <remarks>
    /// Read millions of times per analytics report, so it is converted by arithmetic, which is
    /// exactly what <see cref="DateTimeOffset.ToUnixTimeMilliseconds"/> computes for a zero offset.
    /// </remarks>
    public long Mills => _timestamp.Ticks / TimeSpan.TicksPerMillisecond - UnixEpochMilliseconds;

    private const long UnixEpochMilliseconds = 62_135_596_800_000;

    /// <inheritdoc />
    public int? UtcOffset { get; set; }

    /// <inheritdoc />
    public string? Device { get; set; }

    /// <inheritdoc />
    public string? App { get; set; }

    /// <inheritdoc />
    public string? DataSource { get; set; }

    /// <inheritdoc />
    public Guid? CorrelationId { get; set; }

    /// <inheritdoc />
    public string? LegacyId { get; set; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime ModifiedAt { get; set; }

    /// <inheritdoc />
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}

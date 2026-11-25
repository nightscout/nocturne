namespace Nocturne.Core.Models.V4;

/// <summary>
/// The earliest arrival among the stored readings that one writer inserted with one data source and
/// device; any of the three may be null.
/// </summary>
public sealed record GlucoseStreamArrival(
    Guid? WriterGrantId, string? DataSource, string? Device, DateTime FirstArrival);

/// <summary>A stored reading's timestamp and the writer, data source and device it came with.</summary>
public sealed record GlucoseStreamReading(
    Guid Id, DateTime Timestamp, Guid? WriterGrantId, string? DataSource, string? Device);

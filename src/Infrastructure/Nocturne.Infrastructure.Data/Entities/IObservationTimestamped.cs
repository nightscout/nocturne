namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// An entity carrying the instant its measurement was observed, as UTC — the domain timestamp a
/// record is ordered, range-queried and watermarked on, as opposed to <see cref="ISystemTimestamped"/>,
/// which records when the row was written.
/// </summary>
/// <remarks><inheritdoc cref="IOriginalIdentified" path="/remarks"/></remarks>
public interface IObservationTimestamped
{
    /// <summary>When the measurement was taken, in UTC.</summary>
    DateTime Timestamp { get; set; }
}

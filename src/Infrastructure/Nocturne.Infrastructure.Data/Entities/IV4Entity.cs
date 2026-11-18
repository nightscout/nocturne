namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marker for V4 clinical entities: <see cref="ILegacyIdentified"/> rows that also carry the
/// <see cref="CorrelationId"/> linking the records one legacy upload was decomposed into.
/// </summary>
public interface IV4Entity : ILegacyIdentified
{
    /// <summary>Links records decomposed from the same legacy Treatment or DeviceStatus.</summary>
    Guid? CorrelationId { get; set; }
}

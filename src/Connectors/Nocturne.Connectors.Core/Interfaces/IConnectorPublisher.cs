namespace Nocturne.Connectors.Core.Interfaces;

public interface IConnectorPublisher
{
    bool IsAvailable { get; }
    IGlucosePublisher Glucose { get; }
    ITreatmentPublisher Treatments { get; }
    IDevicePublisher Device { get; }
    IMetadataPublisher Metadata { get; }

    /// <summary>
    /// Records this publisher's scope has left unwritten so far because the user had deleted them.
    /// Cumulative, so a run reads the difference across itself.
    /// </summary>
    int SkippedDeleted { get; }
}

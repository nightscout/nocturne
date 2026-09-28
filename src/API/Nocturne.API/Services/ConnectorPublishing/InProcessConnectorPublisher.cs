using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// In-process implementation of <see cref="IConnectorPublisher"/> that wires connector output
/// directly to the running API services without HTTP or message-queue overhead.
/// Used when connectors run in the same process as the API (the default Aspire configuration).
/// </summary>
/// <seealso cref="IConnectorPublisher"/>
/// <seealso cref="IGlucosePublisher"/>
/// <seealso cref="ITreatmentPublisher"/>
/// <seealso cref="IDevicePublisher"/>
/// <seealso cref="IMetadataPublisher"/>
public class InProcessConnectorPublisher : IConnectorPublisher
{
    private readonly PublishSkipTally _skips;

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public IGlucosePublisher Glucose { get; }

    /// <inheritdoc />
    public ITreatmentPublisher Treatments { get; }

    /// <inheritdoc />
    public IDevicePublisher Device { get; }

    /// <inheritdoc />
    public IMetadataPublisher Metadata { get; }

    /// <inheritdoc />
    public int SkippedDeleted => _skips.SkippedDeleted;

    /// <summary>
    /// Initializes a new instance of <see cref="InProcessConnectorPublisher"/>.
    /// </summary>
    /// <param name="glucose">The glucose publisher for incoming CGM readings.</param>
    /// <param name="treatments">The treatment publisher for bolus, basal, and carb data.</param>
    /// <param name="device">The device publisher for device status and metadata.</param>
    /// <param name="metadata">The metadata publisher for connector-level metadata.</param>
    /// <param name="skips">The scope's tally the four publishers add to.</param>
    public InProcessConnectorPublisher(
        IGlucosePublisher glucose,
        ITreatmentPublisher treatments,
        IDevicePublisher device,
        IMetadataPublisher metadata,
        PublishSkipTally skips)
    {
        _skips = skips;
        Glucose = glucose;
        Treatments = treatments;
        Device = device;
        Metadata = metadata;
    }
}

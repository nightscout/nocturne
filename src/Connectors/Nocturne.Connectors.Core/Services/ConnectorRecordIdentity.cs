using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     The identity every record a connector publishes carries, stamped on the way out so no
///     connector mints an id, reads the clock for a creation time or names its own data source.
/// </summary>
/// <remarks>
///     An id is minted only where none is set: a connector that keys an upsert on a deterministic id
///     (Glooko's patient devices) keeps it, and one that reads a record's id after publishing (Glooko
///     matching a food entry to its carb intake) reads the id the record was published with, which is the
///     stored id only when the record was inserted. The data source is always the
///     connector's own: every record type stamped here is one each connector stores under its
///     registered source. A type not listed (a device status, an activity) keeps what its connector
///     set; see the connector and the API publisher for why.
/// </remarks>
internal static class ConnectorRecordIdentity
{
    public static void Stamp(object record, string source, DateTime now)
    {
        switch (record)
        {
            case IV4Record v4:
                if (v4.Id == Guid.Empty) v4.Id = Guid.CreateVersion7();
                if (v4.CreatedAt == default) v4.CreatedAt = now;
                if (v4.ModifiedAt == default) v4.ModifiedAt = now;
                v4.DataSource = source;
                break;
            case PatientDevice device:
                if (device.Id == Guid.Empty) device.Id = Guid.CreateVersion7();
                if (device.CreatedAt == default) device.CreatedAt = now;
                break;
            case SystemEvent systemEvent:
                systemEvent.Source = source;
                break;
            case BodyWeight bodyWeight:
                bodyWeight.DataSource = source;
                break;
            case HeartRate heartRate:
                heartRate.DataSource = source;
                break;
            case StepCount stepCount:
                stepCount.DataSource = source;
                break;
        }
    }
}

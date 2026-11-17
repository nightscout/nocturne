using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Oura.Configurations;

/// <summary>
/// Oura has no sync loop: its data arrives as a data export archive the user uploads, which
/// <see cref="Export.OuraExportArchive"/> reads.
/// </summary>
[ConnectorRegistration(
    OuraConnectorConfiguration.Name, ServiceNames.OuraConnector, "OURA", nameof(ConnectSource.Oura),
    DataSources.OuraConnector, "oura", ConnectorCategory.Sync,
    "Import sleep, heart rate and steps from an Oura data export", "Oura",
    SupportsHistoricalSync = false,
    SupportsManualSync = false,
    SupportedDataTypes = [SyncDataType.Sleep, SyncDataType.HeartRate, SyncDataType.Steps])]
public sealed class OuraConnectorConfiguration : BaseConnectorConfiguration
{
    public const string Name = "Oura";

    public OuraConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Oura;
    }
}

namespace Nocturne.Core.Models.V4;

/// <summary>
/// The rows one legacy device status was decomposed into, at least one of them present.
/// </summary>
/// <param name="ModifiedAt">
/// The newest change to any row of the status. A history read counts its deleted rows too, which a
/// live status does not carry.
/// </param>
public sealed record DeviceStatusGroup(
    ApsSnapshot? Aps,
    PumpSnapshot? Pump,
    UploaderSnapshot? Uploader,
    DeviceStatusExtras? Extras,
    DateTime ModifiedAt);

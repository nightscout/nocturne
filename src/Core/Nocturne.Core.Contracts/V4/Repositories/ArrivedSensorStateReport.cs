using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// A <see cref="SensorStateReport"/> with when it arrived, as far as it can be told: the earlier of
/// its timestamp and when it was stored, so a report dated ahead arrived when it was stored.
/// </summary>
public sealed record ArrivedSensorStateReport(SensorStateReport Report, DateTime ArrivedAt);

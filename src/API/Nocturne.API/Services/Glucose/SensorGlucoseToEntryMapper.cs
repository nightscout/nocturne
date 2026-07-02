using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Projects a V4 <see cref="SensorGlucose"/> record into the legacy <see cref="Entry"/> shape
/// (type <c>sgv</c>) used by the v1/v2/v3 API surface and the real-time <c>entries</c> SignalR
/// collection that the web dashboard subscribes to.
/// </summary>
/// <remarks>
/// Used by the read side (<see cref="Nocturne.API.Services.V4.V4ToLegacyProjectionService"/>). The
/// realtime <c>entries</c> broadcast now projects from the repository chokepoint via
/// <c>Nocturne.Core.Models.Projections.EntryProjection</c>, which is the canonical projection (it sets
/// the entry id to <c>legacyId ?? Guid</c> so broadcasts merge against reads on the same id).
/// </remarks>
public static class SensorGlucoseToEntryMapper
{
    /// <summary>
    /// Converts a <see cref="SensorGlucose"/> reading into the legacy <see cref="Entry"/> shape.
    /// </summary>
    /// <param name="sg">The V4 sensor glucose record.</param>
    /// <returns>An <see cref="Entry"/> of type <c>sgv</c> populated from the reading.</returns>
    public static Entry ToEntry(SensorGlucose sg) =>
        new()
        {
            Id = sg.Id.ToString(),
            Type = "sgv",
            Mills = sg.Mills,
            Sgv = sg.Mgdl,
            Mgdl = sg.Mgdl,
            Mmol = sg.Mmol,
            Mbg = 0,
            Direction = sg.Direction?.ToString(),
            Trend = sg.Trend.HasValue ? (int?)sg.Trend.Value : null,
            TrendRate = sg.TrendRate,
            Noise = sg.Noise,
            Device = sg.Device,
            App = sg.App,
            DataSource = sg.DataSource,
        };
}

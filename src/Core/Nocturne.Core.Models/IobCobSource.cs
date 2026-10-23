using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>Where an IOB or COB value came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IobCobSource>))]
public enum IobCobSource
{
    /// <summary>Reported by the AID system in a recent APS snapshot.</summary>
    Device,

    /// <summary>Computed by Nocturne from the treatments.</summary>
    Calculated,
}

using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>Where the insulin action time (DIA) in use comes from, in the order it is resolved.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InsulinActionTimeSource>))]
public enum InsulinActionTimeSource
{
    /// <summary>The active profile is managed by another app (e.g. Glooko, mylife), whose value always wins.</summary>
    ExternalProfile,

    /// <summary>The current primary bolus insulin.</summary>
    PrimaryInsulin,

    /// <summary>The active profile's own value.</summary>
    Profile,

    /// <summary>Nothing sets one, so the built-in default applies.</summary>
    Default,
}

/// <summary>The insulin action time Nocturne uses for insulin on board and predictions.</summary>
/// <param name="Hours">The action time in hours.</param>
/// <param name="PrimaryInsulinName">
/// The current primary bolus insulin, whether or not its action time is the one in use.
/// </param>
public record InsulinActionTime(InsulinActionTimeSource Source, double Hours, string? PrimaryInsulinName);

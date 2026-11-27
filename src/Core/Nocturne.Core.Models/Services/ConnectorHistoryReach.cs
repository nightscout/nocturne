using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Services;

/// <summary>The unit of a <see cref="ConnectorHistoryReach"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HistoryReachUnit>))]
public enum HistoryReachUnit
{
    /// <summary>The source serves its whole history: the reach has no amount.</summary>
    Whole = 0,
    Hours,
    Days,
    Months,
}

/// <summary>
/// How far back a connector's source serves records, as an amount of one unit or the whole history.
/// </summary>
/// <param name="Unit">What <paramref name="Amount"/> counts.</param>
/// <param name="Amount">How many <paramref name="Unit"/>s back; zero for <see cref="HistoryReachUnit.Whole"/>.</param>
public sealed record ConnectorHistoryReach(
    [property: JsonPropertyName("unit")] HistoryReachUnit Unit,
    [property: JsonPropertyName("amount")] int Amount)
{
    /// <summary>The source serves its whole history.</summary>
    public static ConnectorHistoryReach Whole { get; } = new(HistoryReachUnit.Whole, 0);

    /// <summary>The oldest instant this reach covers from <paramref name="now"/>, or null for the whole history.</summary>
    public DateTime? StartFrom(DateTime now) => Unit switch
    {
        HistoryReachUnit.Hours => now.AddHours(-Amount),
        HistoryReachUnit.Days => now.AddDays(-Amount),
        HistoryReachUnit.Months => now.AddMonths(-Amount),
        _ => null,
    };
}

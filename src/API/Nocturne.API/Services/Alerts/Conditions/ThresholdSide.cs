namespace Nocturne.API.Services.Alerts.Conditions;

/// <summary>
/// The side of its limit a <c>threshold</c> condition alerts on, read from its operator:
/// <c>below</c> for <c>&lt;</c> and <c>&lt;=</c>, <c>above</c> for <c>&gt;</c> and <c>&gt;=</c>, none for
/// <c>==</c> or an operator naming no member.
/// </summary>
internal static class ThresholdSide
{
    public const string Below = "below";
    public const string Above = "above";

    public static string? Of(string? @operator) => @operator switch
    {
        "<" or "<=" => Below,
        ">" or ">=" => Above,
        _ => null,
    };
}

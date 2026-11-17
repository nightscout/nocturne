namespace Nocturne.Core.Models;

/// <summary>
/// Named sets of <see cref="StateSpanCategory"/> values shared by readers that must agree on membership.
/// </summary>
public static class StateSpanCategories
{
    /// <summary>
    /// Categories drawn as activity spans: the user-annotated periods a chart or timeline overlays on glucose.
    /// </summary>
    public static readonly IReadOnlyList<StateSpanCategory> DrawnActivities =
    [
        StateSpanCategory.Exercise,
        StateSpanCategory.Illness,
        StateSpanCategory.Travel,
    ];
}

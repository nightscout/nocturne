using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>Which of a set of <see cref="ProfileActivation"/>s applies at each moment.</summary>
/// <remarks>
/// Among the activations running at a moment the newest start wins, whether permanent or temporary,
/// as Nightscout's <c>activeProfileTreatmentToTime</c> (lib/profilefunctions.js) and AAPS's
/// <c>AppRepository.getProfileSwitchActiveAt</c> pick. A permanent activation keeps running under a
/// temporary one and applies again once the temporary one ends. A source that has reported what its
/// pump runs is read only from those reports from its first one on: its requested activations starting
/// then or later never apply on their own, while those before it, from before it reported, still do.
/// A switch recorded in Nocturne (<see cref="ProfileActivationSource.IsNative"/>) is never one of a
/// source's requests, but it gives way to what an AID system reports: at a moment an effective
/// activation is in force, recorded switches do not apply, since the pump runs what it reports. They
/// apply where nothing reports, as for someone on injections.
/// </remarks>
internal static class ProfileActivationSelection
{
    /// <param name="firstEffectiveMills">
    /// The start of the earliest effective activation of each <see cref="ProfileActivationSource"/> key
    /// that has written one.
    /// </param>
    public static List<ProfileActivation> Eligible(
        IEnumerable<ProfileActivation> activations, IReadOnlyDictionary<string, long> firstEffectiveMills) =>
        activations
            .Where(a => a.Phase == ProfileActivationPhase.Effective
                || ProfileActivationSource.IsNative(a)
                || !firstEffectiveMills.TryGetValue(ProfileActivationSource.KeyOf(a), out var first)
                || a.Mills < first)
            .ToList();

    public static ProfileActivation? At(IReadOnlyList<ProfileActivation> eligible, long timeMills)
    {
        var covering = eligible.Where(a => Covers(a, timeMills)).ToList();
        var reported = covering.Any(a => a.Phase == ProfileActivationPhase.Effective);
        return Newest(covering.Where(a => !reported || !ProfileActivationSource.IsNative(a)));
    }

    /// <summary>The newest effective activation in force at <paramref name="timeMills"/>: what an AID system reports its pump runs.</summary>
    public static ProfileActivation? ReportedAt(IEnumerable<ProfileActivation> activations, long timeMills) =>
        Newest(activations.Where(a => a.Phase == ProfileActivationPhase.Effective && Covers(a, timeMills)));

    /// <summary>The newest switch recorded in Nocturne in force at <paramref name="timeMills"/>, whether or not it applies.</summary>
    public static ProfileActivation? NativeAt(IEnumerable<ProfileActivation> activations, long timeMills) =>
        Newest(activations.Where(a => ProfileActivationSource.IsNative(a) && Covers(a, timeMills)));

    private static ProfileActivation? Newest(IEnumerable<ProfileActivation> activations) =>
        activations
            .OrderByDescending(a => a.Mills)
            .ThenByDescending(a => a.Id)
            .FirstOrDefault();

    /// <summary>
    /// The stretches of [<paramref name="fromMills"/>, <paramref name="toMills"/>) one activation of
    /// <paramref name="eligible"/> applies over, merged where it continues, gaps left out.
    /// </summary>
    public static List<(long Start, long End, ProfileActivation Active)> Segments(
        IReadOnlyList<ProfileActivation> eligible, long fromMills, long toMills)
    {
        var boundaries = new SortedSet<long> { fromMills, toMills };
        foreach (var activation in eligible)
        {
            if (activation.Mills > fromMills && activation.Mills < toMills)
                boundaries.Add(activation.Mills);
            if (EndOf(activation) is { } end && end > fromMills && end < toMills)
                boundaries.Add(end);
        }

        var segments = new List<(long Start, long End, ProfileActivation Active)>();
        var points = boundaries.ToList();
        for (var i = 0; i < points.Count - 1; i++)
        {
            if (At(eligible, points[i]) is not { } active)
                continue;

            if (segments.Count > 0 && segments[^1].End == points[i] && segments[^1].Active.Id == active.Id)
                segments[^1] = (segments[^1].Start, points[i + 1], active);
            else
                segments.Add((points[i], points[i + 1], active));
        }

        return segments;
    }

    /// <summary>
    /// Where an activation stops applying: its end, or for a temporary one written without an end, the
    /// length it asked for.
    /// </summary>
    public static long? EndOf(ProfileActivation activation) =>
        activation.EndMills
        ?? (activation.RequestedDurationMs is > 0 and var duration ? activation.Mills + duration : null);

    private static bool Covers(ProfileActivation activation, long timeMills) =>
        activation.Mills <= timeMills && (EndOf(activation) is not { } end || end > timeMills);
}

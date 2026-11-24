namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// How oref shortens its forecast curves, and how a reader carries a shortened curve back out to a
/// length: oref0 <c>lib/determine-basal/determine-basal.js:653-690</c> at
/// <c>88cf032aa74ff25f69464a7d9cd601ee3940c0b3</c>.
/// </summary>
/// <remarks>
/// oref pops trailing equal values from its IOB, COB and UAM curves while more than
/// <see cref="LevelTrimmedLength"/> remain, so past its end such a curve holds its last value
/// exactly. Its zero-temp curve is cut where it turns up above target instead: past its end it is
/// rising, so its last value held is a lower bound of the tail. Over a list that holds it, that is
/// exact for the lowest value compared with <c>&lt;</c> or <c>&lt;=</c>: a held value cannot make
/// that comparison true, and it cannot hide a lower value of another curve. Every other comparison
/// can read too low: a value above a limit that only the zero-temp tail gives is missed, and "every
/// curve below a limit" can hold where the real tail is higher.
/// </remarks>
public static class OrefCurveTails
{
    /// <summary>The fewest values oref leaves when it drops a level tail from its IOB, COB and UAM curves.</summary>
    public const int LevelTrimmedLength = 13;

    /// <summary>
    /// Whether an IOB, COB or UAM curve of <paramref name="count"/> values had its level tail dropped
    /// before <paramref name="length"/>: a shorter curve simply ends.
    /// </summary>
    public static bool IsLevelTrimmed(int count, int length) => count >= LevelTrimmedLength && count < length;

    /// <summary><paramref name="curve"/> with its last value held out to <paramref name="length"/>.</summary>
    public static T[] HoldLast<T>(IReadOnlyList<T> curve, int length)
    {
        var extended = new T[Math.Max(length, curve.Count)];
        for (var i = 0; i < extended.Length; i++)
            extended[i] = curve[Math.Min(i, curve.Count - 1)];
        return extended;
    }
}

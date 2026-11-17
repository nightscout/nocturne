using System.Globalization;
using System.Text.RegularExpressions;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>What an oref suggestion's <c>reason</c> says about the limits that cut it.</summary>
/// <param name="CappedAtMaxSafeBasal">
/// determine-basal cut the temp it asked for to <c>maxSafeBasal</c>, the lowest of <c>max_basal</c>
/// and the two safety multiples of the schedule ("adj. req. rate: X to maxSafeBasal: Y"). It says
/// so wherever it compares a rate with the cap itself; a reason without it means determine-basal
/// made no such cut, not that the temp is below the cap, since <c>setTempBasal</c> clamps a rate
/// to the cap again without saying so.
/// </param>
/// <param name="AtMaxIob">
/// IOB was at <c>max_iob</c>: the insulin asked for was cut to it ("max_iob N") or IOB already
/// exceeded it ("IOB X &gt; max_iob N").
/// </param>
/// <param name="MaxIob">U, where the reason gives it.</param>
public readonly record struct OrefReason(bool CappedAtMaxSafeBasal, bool AtMaxIob, double? MaxIob)
{
    /// <summary>
    /// Every system writes <c>max_iob</c> with a decimal point (AAPS from a Kotlin string
    /// template, not its locale-formatted numbers). A decimal comma is accepted anyway; a comma
    /// followed by a space is the reason's own separator.
    /// </summary>
    private static readonly Regex MaxIobPattern = new(@"max_iob\s+(\d+(?:[.,]\d+)?)", RegexOptions.CultureInvariant);

    /// <param name="limitReason">See <see cref="ApsLoopCycle.LimitReason"/>.</param>
    public static OrefReason Parse(string? limitReason)
    {
        if (string.IsNullOrEmpty(limitReason))
            return default;

        var maxIob = MaxIobPattern.Match(limitReason);
        return new OrefReason(
            limitReason.Contains(ApsLoopCycle.MaxSafeBasalToken, StringComparison.Ordinal),
            limitReason.Contains(ApsLoopCycle.MaxIobToken, StringComparison.Ordinal),
            maxIob.Success
                ? double.Parse(maxIob.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
                : null);
    }
}

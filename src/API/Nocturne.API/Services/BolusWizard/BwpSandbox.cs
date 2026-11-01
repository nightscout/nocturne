using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.BolusWizard;

/// <summary>
/// Profile values already resolved at the sandbox's time, so <see cref="BwpSandbox"/> can answer
/// the synchronous <see cref="IBwpProfile"/> calls.
/// </summary>
public sealed record BwpProfile(double Sensitivity, double LowTarget, double HighTarget, double TotalBasal)
    : IBwpProfile
{
    public bool HasData() => true;
    public double GetSensitivity(long time, string? specProfile = null) => Sensitivity;
    public double GetHighBGTarget(long time, string? specProfile = null) => HighTarget;
    public double GetLowBGTarget(long time, string? specProfile = null) => LowTarget;
    public double GetTotalBasal(long time, string? specProfile = null) => TotalBasal;
}

/// <summary>
/// The parts of Nightscout's sandbox <c>boluswizardpreview.js</c> reads, in mg/dl.
/// </summary>
/// <param name="LastSgv">The newest reading, Nightscout's <c>_.last(sbx.data.sgvs)</c>.</param>
/// <param name="Iob">The <c>iob</c> property; null when it is not offered.</param>
/// <param name="Profile">Null when there is no treatment profile.</param>
public sealed record BwpSandbox(
    long Time,
    Entry? LastSgv,
    IobResult? Iob,
    List<CarbIntake> CarbIntakes,
    BwpProfile? Profile
) : IBwpSandbox
{
    private const long CurrentForMills = 15 * 60 * 1000;

    public Entry? LastSGVEntry() => LastSgv;

    public double? LastScaledSGV() => LastSgv?.GlucoseMgdl();

    public bool IsCurrent(Entry entry) => Time - entry.Mills <= CurrentForMills;

    public List<CarbIntake> GetCarbIntakes() => CarbIntakes;

    public IBwpProfile? GetProfile() => Profile;

    /// <summary>Nightscout's <c>roundInsulinForDisplayFormat</c>, which floors to the hundredth.</summary>
    public string RoundInsulinForDisplayFormat(double insulin) =>
        insulin == 0 ? "0" : JsNumber.ToFixed2(Math.Floor(insulin / 0.01) * 0.01);

    /// <summary>Nightscout's <c>roundBGToDisplayFormat</c> in mg/dl: JavaScript's <c>Math.round</c>.</summary>
    public double RoundBGToDisplayFormat(double bg) => Math.Floor(bg + 0.5);
}

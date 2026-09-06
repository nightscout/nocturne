using Nocturne.Core.Models;

namespace Nocturne.Core.Models.Sleep.Report;

public class SleepSingleNightReport
{
    public SleepSession Session { get; set; } = null!;
    /// <summary>
    /// Resolved sleep score: device-provided when available, computed from stage data otherwise.
    /// Null when there is no stage data to compute from.
    /// See <see cref="ScoreSource"/> to determine the origin.
    /// </summary>
    public int? Score { get; set; }
    public SleepScoreSource ScoreSource { get; set; }
    public SleepStageBreakdown StageBreakdown { get; set; } = null!;
    /// <summary>Null when no CGM data overlaps the session window.</summary>
    public SleepOvernightTir? OvernightTir { get; set; }
    public IReadOnlyList<SleepHypoEvent> HypoEvents { get; set; } = [];
    /// <summary>Null when fewer than 4 CGM readings exist in the final 2-hour pre-wake window.</summary>
    public SleepDawnPhenomenon? DawnPhenomenon { get; set; }
    public IReadOnlyList<SleepWakeEvent> WakeEvents { get; set; } = [];
}

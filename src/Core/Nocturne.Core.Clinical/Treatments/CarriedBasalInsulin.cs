using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// Temp basal insulin still to act at an instant, on the model net insulin above schedule is
/// measured on: each five minutes of a piece carries its delivery less the schedule's, negative
/// below it, and that decays linearly over DIA from the start of those five minutes.
/// </summary>
/// <remarks>
/// Displayed IOB (<see cref="IobCalculator.FromTempBasals(List{TempBasal}, TherapySnapshot, long)"/>)
/// keeps oref's model, which counts only delivery above the schedule and decays a piece's whole
/// excess from its start. A reader that subtracts IOB from net insulin above schedule needs this
/// one: under oref's, a zero temp carries no IOB, and a long piece's IOB reads low and falls to
/// zero at DIA while the temp still runs.
/// </remarks>
public static class CarriedBasalInsulin
{
    private const long StepMs = 5 * 60 * 1000;

    /// <summary>
    /// The insulin <paramref name="pieces"/> carry at <paramref name="time"/>, U. Pieces are as
    /// <see cref="Statistics.StatisticsService.ClipOverlappingTempBasals"/> returns them; a piece
    /// of unknown rate carries nothing.
    /// </summary>
    public static double At(IEnumerable<TempBasal> pieces, TherapySnapshot snapshot, long time)
    {
        double carried = 0;
        foreach (var piece in pieces)
        {
            if ((piece.Origin == TempBasalOrigin.Suspended ? 0 : piece.Rate) is not { } rate)
                continue;
            var diaMs = (piece.InsulinContext?.Dia ?? snapshot.Dia) * 60 * 60 * 1000;
            var end = Math.Min(time, piece.EndMills ?? piece.StartMills);
            var acted = time - (long)diaMs - piece.StartMills;
            for (var start = piece.StartMills + Math.Max(0, acted / StepMs) * StepMs; start < end; start += StepMs)
            {
                var stop = Math.Min(start + StepMs, end);
                var scheduled = piece.ScheduledRate ?? snapshot.BasalRateAt(start);
                var units = (rate - scheduled) * (stop - start) / (60.0 * 60 * 1000);
                carried += units * Math.Max(0, 1 - (time - start) / diaMs);
            }
        }
        return carried;
    }
}

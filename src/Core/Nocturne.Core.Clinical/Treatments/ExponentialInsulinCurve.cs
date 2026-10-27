namespace Nocturne.Core.Clinical.Treatments;

/// <summary>
/// oref0's exponential insulin curve (<c>iobCalcExponential</c>), as AAPS and Trio use it: time,
/// peak and DIA in real minutes. It is defined only for a peak under half the DIA.
/// </summary>
public static class ExponentialInsulinCurve
{
    /// <summary>The fraction of a dose still on board <paramref name="minutes"/> after delivery; 0 from DIA on.</summary>
    public static double IobFraction(double minutes, double peakMinutes, double diaMinutes)
    {
        if (minutes >= diaMinutes)
            return 0;

        var (tau, a, s) = Shape(peakMinutes, diaMinutes);
        return 1 - s * (1 - a)
            * ((minutes * minutes / (tau * diaMinutes * (1 - a)) - minutes / tau - 1) * Math.Exp(-minutes / tau) + 1);
    }

    /// <summary>The fraction of a dose acting per minute <paramref name="minutes"/> after delivery: minus the time derivative of <see cref="IobFraction"/>.</summary>
    public static double ActivityFraction(double minutes, double peakMinutes, double diaMinutes)
    {
        if (minutes >= diaMinutes)
            return 0;

        var (tau, _, s) = Shape(peakMinutes, diaMinutes);
        return s / (tau * tau) * minutes * (1 - minutes / diaMinutes) * Math.Exp(-minutes / tau);
    }

    private static (double Tau, double A, double S) Shape(double peakMinutes, double diaMinutes)
    {
        var tau = peakMinutes * (1 - peakMinutes / diaMinutes) / (1 - 2 * peakMinutes / diaMinutes);
        var a = 2 * tau / diaMinutes;
        var s = 1 / (1 - a + (1 + a) * Math.Exp(-diaMinutes / tau));
        return (tau, a, s);
    }
}

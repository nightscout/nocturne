using Nocturne.Core.Constants;

namespace Nocturne.Core.Models;

public class A1cDisplayValue
{
    public double Percent { get; init; }
    public double MmolMol { get; init; }

    // Keep the existing NGSP/IFCC relationship in one place, including lab input.
    public static A1cDisplayValue FromPercent(double percent) => new()
    {
        Percent = percent,
        MmolMol = (percent - 2.15) * 10.929,
    };

    public static double ToPercent(double mmolMol) => mmolMol / 10.929 + 2.15;
}

/// <summary>
/// Whether a value is below its target, judged separately in each unit at the precision that unit
/// is displayed at: one decimal for percent, a whole number for mmol/mol. Near a boundary the two
/// can differ, so each unit's display is paired with its own state.
/// </summary>
public class A1cTargetState
{
    public bool Percent { get; init; }
    public bool MmolMol { get; init; }

    public static A1cTargetState Below(A1cDisplayValue value, A1cDisplayValue target) => new()
    {
        Percent = Math.Round(value.Percent, 1, MidpointRounding.AwayFromZero)
            < Math.Round(target.Percent, 1, MidpointRounding.AwayFromZero),
        MmolMol = Math.Round(value.MmolMol, 0, MidpointRounding.AwayFromZero)
            < Math.Round(target.MmolMol, 0, MidpointRounding.AwayFromZero),
    };
}

public class A1cDisplayReferences
{
    public A1cDisplayValue Minimum => A1cDisplayValue.FromPercent(4);
    public A1cDisplayValue Healthy => A1cDisplayValue.FromPercent(5.7);
    public A1cDisplayValue Target => A1cDisplayValue.FromPercent(GlucoseConstants.A1cTargetPercent);
    public A1cDisplayValue Elevated => A1cDisplayValue.FromPercent(9);
    public A1cDisplayValue VeryHigh => A1cDisplayValue.FromPercent(14);
    public double MmolMolPadding => A1cDisplayValue.FromPercent(4.5).MmolMol - Minimum.MmolMol;
}

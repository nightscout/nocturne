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

public class A1cDisplayReferences
{
    public A1cDisplayValue Minimum => A1cDisplayValue.FromPercent(4);
    public A1cDisplayValue Healthy => A1cDisplayValue.FromPercent(5.7);
    public A1cDisplayValue Target => A1cDisplayValue.FromPercent(7);
    public A1cDisplayValue Elevated => A1cDisplayValue.FromPercent(9);
    public A1cDisplayValue VeryHigh => A1cDisplayValue.FromPercent(14);
    public double MmolMolPadding => A1cDisplayValue.FromPercent(4.5).MmolMol - Minimum.MmolMol;
}

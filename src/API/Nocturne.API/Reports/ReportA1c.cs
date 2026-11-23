using Nocturne.Core.Models;

namespace Nocturne.API.Reports;

/// <summary>An HbA1c percent in the caller's HbA1c unit (<see cref="ReportDatasetRequest.A1cMmolMol"/>).</summary>
internal static class ReportA1c
{
    /// <summary>The value a row holds: percent to two decimals, or mmol/mol to one, as the eHbA1c page shows them.</summary>
    public static double Value(double percent, bool mmolMol) =>
        mmolMol ? ScaledRounding.Round(A1cDisplayValue.FromPercent(percent).MmolMol, 1) : ScaledRounding.Round(percent, 2);

    /// <summary>The value in words, without its unit.</summary>
    public static string Text(double percent, bool mmolMol) =>
        mmolMol ? ReportFacts.Number(A1cDisplayValue.FromPercent(percent).MmolMol, "0.0") : ReportFacts.Number(percent, "0.0#");

    /// <summary>The value in words with its unit: <c>6.5%</c> or <c>47.5 mmol/mol</c>.</summary>
    public static string WithUnit(double percent, bool mmolMol) =>
        mmolMol ? $"{Text(percent, true)} mmol/mol" : $"{Text(percent, false)}%";

    public static string Unit(bool mmolMol) => mmolMol ? "mmol/mol" : "%";
}

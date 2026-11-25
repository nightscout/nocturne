using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports;

internal static class ReportGlucose
{
    /// <summary>A mg/dL value in the report's display unit: whole mg/dL, or mmol/L to one decimal.</summary>
    public static double Scale(double mgdl, bool useMmol) =>
        GlucoseUnits.FromMgdl(mgdl, useMmol ? GlucoseUnit.Mmol : GlucoseUnit.MgDl);
}

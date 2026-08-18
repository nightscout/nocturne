namespace Nocturne.Core.Constants;

/// <summary>
/// Glucose unit conversion constants shared by models, connectors, analytics, and widgets.
/// </summary>
public static class GlucoseConstants
{
    /// <summary>
    /// Milligrams per decilitre in one millimole per litre of glucose: the conventional CGM-ecosystem
    /// factor, about 1 part in 7,000 above the 180.156 g/mol molar mass divided by 10.
    /// </summary>
    public const double MgdlPerMmol = 18.0182;
}

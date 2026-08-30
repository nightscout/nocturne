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

    /// <summary>
    /// Bottom of the consensus in-range band in mg/dL — the boundary time-in-range is measured
    /// against and below which a reading is drawn as low, before any tenant threshold overrides it.
    /// Not the legacy Nightscout <c>bgTargetBottom</c> alarm setting, which ships 80; that one is
    /// <see cref="ApplicationConstants.Web.Thresholds.BgTargetBottom"/>.
    /// </summary>
    public const double TargetBottomMgdl = 70;

    /// <summary>
    /// Top of the consensus in-range band in mg/dL. See <see cref="TargetBottomMgdl"/>.
    /// </summary>
    public const double TargetTopMgdl = 180;

    /// <summary>
    /// Clinically significant hypoglycaemia in mg/dL: the level-2 boundary the consensus reports
    /// time below, distinct from the level-1 boundary at <see cref="TargetBottomMgdl"/>.
    /// </summary>
    public const double VeryLowMgdl = 54;

    /// <summary>
    /// Clinically significant hyperglycaemia in mg/dL. See <see cref="VeryLowMgdl"/>.
    /// </summary>
    public const double VeryHighMgdl = 250;

    /// <summary>
    /// Tile fill per glucose status as <c>RRGGBB</c>, for the native surfaces that paint a reading
    /// directly — the desktop tray tile and the taskbar sparkline — instead of resolving the web
    /// theme's status tokens.
    /// </summary>
    public static class StatusPalette
    {
        /// <summary>Between <see cref="TargetBottomMgdl"/> and <see cref="TargetTopMgdl"/>.</summary>
        public const string InRange = "36C76A";

        /// <summary>Above <see cref="TargetTopMgdl"/>.</summary>
        public const string High = "E6B800";

        /// <summary>Below <see cref="TargetBottomMgdl"/>.</summary>
        public const string Low = "E0533D";
    }
}

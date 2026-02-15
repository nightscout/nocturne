namespace Nocturne.Connectors.Tidepool.Configurations;

public static class TidepoolConstants
{
    /// <summary>
    ///     Conversion factor for mmol/L to mg/dL (molar mass of glucose / 10)
    /// </summary>
    public const double MmolToMgdlFactor = 18.01559;

    public static class Servers
    {
        public const string Us = "api.tidepool.org";
        public const string Development = "dev-api.tidepool.org";
    }

    public static class Headers
    {
        public const string SessionToken = "x-tidepool-session-token";
    }

    public static class DataTypes
    {
        public const string Cbg = "cbg";
        public const string Smbg = "smbg";
        public const string Bolus = "bolus";
        public const string Food = "food";
        public const string PhysicalActivity = "physicalActivity";
        public const string PumpSettings = "pumpSettings";
    }
}

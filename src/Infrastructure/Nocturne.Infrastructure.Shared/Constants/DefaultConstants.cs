namespace Nocturne.Infrastructure.Shared.Constants;

/// <summary>
/// Default values used throughout the application
/// </summary>
public static class DefaultConstants
{
    /// <summary>
    /// Core application defaults
    /// </summary>
    public static class Core
    {
        public const string MongoCollection = "entries";
        public const string MongoProfileCollection = "profile";
        public const string Hostname = "0.0.0.0";
        public const string DisplayUnits = "mmol";
        public const int TimeFormat = 24;
        public const string Language = "en";
        public const string NodeEnvironment = "development";
        public const int AuthFailDelay = 50;
        public const string DataDirectory = "./data";
    }

    /// <summary>
    /// Loop defaults
    /// </summary>
    public static class Loop
    {
        public const string PushServerEnvironment = "development";
    }
}

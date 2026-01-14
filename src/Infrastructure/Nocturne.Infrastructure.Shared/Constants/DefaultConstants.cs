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
    /// Connector defaults
    /// </summary>
    public static class Connectors
    {
        /// <summary>
        /// Glooko defaults
        /// </summary>
        public static class Glooko
        {
            public const string Server = "eu.api.glooko.com";
            public const int TimezoneOffset = 0;
        }

        /// <summary>
        /// CareLink defaults
        /// </summary>
        public static class CareLink
        {
            public const string Region = "us";
        }

        /// <summary>
        /// Dexcom defaults
        /// </summary>
        public static class Dexcom
        {
            public const string Region = "us";
        }

        /// <summary>
        /// LibreLinkUp defaults
        /// </summary>
        public static class LibreLinkUp
        {
            public const string Region = "EU";
        }

        public static class MyLife
        {
            public const int AppPlatform = 2;
            public const int AppVersion = 20403;
            public const int SyncMonths = 6;
        }
    }

    /// <summary>
    /// Loop defaults
    /// </summary>
    public static class Loop
    {
        public const string PushServerEnvironment = "development";
    }
}

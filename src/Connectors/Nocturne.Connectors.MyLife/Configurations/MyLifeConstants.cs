namespace Nocturne.Connectors.MyLife.Configurations.Constants;

public static class MyLifeConstants
{
    public const string UserLocationServiceUrl =
        "https://cs.mylife-software.net/UserLocationService/MobileUserLocationService.svc";

    public static class SoapActions
    {
        public const string GetUser20 = "http://tempuri.org/IUserLocationService/GetUser20";
        public const string Login = "http://tempuri.org/IMylifeAuthService/Login";
        public const string SyncPatientList = "http://tempuri.org/IMylifeSyncService/SyncPatientList";
        public const string SyncEvents = "http://tempuri.org/IMylifeSyncService/SyncEvents";
    }

    public static class ServicePaths
    {
        public const string AuthService = "mylifeAuthService/MylifeMobileAuthService.svc";
        public const string SyncService = "mylifeSyncService/MylifeMobileSyncService.svc";
    }

    /// <summary>
    ///     Cryptographic constants for decrypting MyLife event data archives.
    /// </summary>
    public static class Crypto
    {
        /// <summary>
        ///     AES-256 CBC encryption key for decrypting MyLife ZIP archives.
        ///     This is a fixed key used by the MyLife mobile application.
        /// </summary>
        public const string ZipAesKey = "x.jweroiwou02ß*";

        /// <summary>
        ///     AES-256 CBC initialization vector for decrypting MyLife ZIP archives.
        ///     Used in conjunction with <see cref="ZipAesKey" /> for CBC mode decryption.
        /// </summary>
        public const string ZipAesIv = "kworugzeqeruwirw";
    }
}
namespace Nocturne.Connectors.MyLife.Constants;

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

    public static class Crypto
    {
        public const string ZipAesKey = "x.jweroiwou02ß*";
        public const string ZipAesIv = "kworugzeqeruwirw";
    }
}
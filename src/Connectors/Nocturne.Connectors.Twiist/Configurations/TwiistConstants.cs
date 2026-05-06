namespace Nocturne.Connectors.Twiist.Configurations;

/// <summary>
/// Constants for the Twiist Insight follower API and AWS Cognito authentication.
/// Reverse-engineered from the Twiist Insight iOS app.
/// </summary>
public static class TwiistConstants
{
    /// <summary>
    /// Base URL for the Twiist follower service API.
    /// </summary>
    public const string FollowerServiceBaseUrl = "https://follower-service.mytwiistportal.com";

    /// <summary>
    /// User-Agent string mimicking the Twiist Insight iOS app.
    /// </summary>
    public const string UserAgent = "twiist insiight/1.0.2 CFNetwork/1568.100.1.2.3 Darwin/24.0.0";

    /// <summary>
    /// Epoch used by Twiist binary blobs (2008-01-01T00:00:00Z).
    /// All blob timestamps are seconds since this epoch.
    /// </summary>
    public static readonly DateTime BlobEpoch = new(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static class Cognito
    {
        public const string PoolId = "us-east-1_fnkWvSdfv";
        public const string ClientId = "65ev2vbkr2mle7uu4cqkn7ohgl";
        public const string BaseUrl = "https://cognito-idp.us-east-1.amazonaws.com/";
        public const string ContentType = "application/x-amz-json-1.1";
        public const string AmzTarget = "AWSCognitoIdentityProviderService.InitiateAuth";
    }
}

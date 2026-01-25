using System.Security.Cryptography;

namespace Nocturne.API.Services.Alerts.Webhooks;

public static class WebhookSecretGenerator
{
    public static string Generate(int bytes = 32)
    {
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }
}

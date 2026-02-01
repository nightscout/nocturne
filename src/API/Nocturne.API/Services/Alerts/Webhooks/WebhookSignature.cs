using System.Security.Cryptography;
using System.Text;

namespace Nocturne.API.Services.Alerts.Webhooks;

public static class WebhookSignature
{
    public static string Create(string secret, string payload, long timestamp)
    {
        var data = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using dotAPNS;
using Nocturne.API.Services.Notifications;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Mock implementation of IApnsClientFactory for testing
/// Creates MockApnsClient instances that capture push data for assertions
/// </summary>
public class MockApnsClientFactory : IApnsClientFactory
{
    private readonly ConcurrentBag<CapturedApnsPush> _capturedPushes = [];

    /// <summary>
    /// Gets all captured push notifications
    /// </summary>
    public IReadOnlyCollection<CapturedApnsPush> CapturedPushes => _capturedPushes.ToArray();

    /// <summary>
    /// Gets the most recent captured push
    /// </summary>
    public CapturedApnsPush? LastPush => _capturedPushes.LastOrDefault();

    /// <summary>
    /// Response to return for push operations (defaults to success)
    /// </summary>
    public ApnsResponse NextResponse { get; set; } = ApnsResponse.Successful();

    public bool IsConfigured => true;

    public IApnsClient? CreateClient(string bundleId)
    {
        return new MockApnsClient(bundleId, this);
    }

    internal void CapturePush(CapturedApnsPush push)
    {
        _capturedPushes.Add(push);
    }

    /// <summary>
    /// Clears all captured pushes
    /// </summary>
    public void Clear()
    {
        _capturedPushes.Clear();
    }
}

/// <summary>
/// Mock APNS client that captures push data instead of sending to Apple
/// Uses ApplePush.GeneratePayload() to extract the actual payload that would be sent
/// </summary>
internal class MockApnsClient : IApnsClient
{
    private readonly string _bundleId;
    private readonly MockApnsClientFactory _factory;

    public MockApnsClient(string bundleId, MockApnsClientFactory factory)
    {
        _bundleId = bundleId;
        _factory = factory;
    }

    [Obsolete("Use SendAsync instead")]
    public Task<ApnsResponse> Send(ApplePush push)
    {
        return SendAsync(push);
    }

    public Task<ApnsResponse> SendAsync(ApplePush push, CancellationToken ct = default)
    {
        // Use the library's public GeneratePayload() method to get the exact payload
        var payloadObj = push.GeneratePayload();
        var payloadJson = JsonSerializer.Serialize(payloadObj);

        // Extract device token - ApplePush has Token property on some versions, or use VoipToken
        // Use JsonDocument to parse and restructure the payload for test assertions
        string deviceToken = "unknown";
        string alert = "";
        var customProperties = new Dictionary<string, string?>();

        try
        {
            // Token/VoipToken are publicly accessible
            deviceToken = push.Token ?? push.VoipToken ?? "unknown";
        }
        catch
        {
            // Token property may not exist in all versions
        }

        // Parse the payload JSON
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        // Extract alert from aps section
        if (root.TryGetProperty("aps", out var aps))
        {
            if (aps.TryGetProperty("alert", out var alertElement))
            {
                alert = alertElement.ValueKind == JsonValueKind.String
                    ? alertElement.GetString() ?? ""
                    : alertElement.GetRawText();
            }
        }

        // Extract all non-aps properties as custom properties
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "aps")
            {
                customProperties[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
            }
        }

        var captured = new CapturedApnsPush
        {
            BundleId = _bundleId,
            DeviceToken = deviceToken,
            Alert = alert,
            CustomProperties = customProperties,
        };

        _factory.CapturePush(captured);

        return Task.FromResult(_factory.NextResponse);
    }
}

/// <summary>
/// Represents a captured APNS push notification for test assertions
/// </summary>
public class CapturedApnsPush
{
    public string BundleId { get; init; } = "";
    public string DeviceToken { get; init; } = "";
    public string Alert { get; init; } = "";
    public Dictionary<string, string?> CustomProperties { get; init; } = [];

    /// <summary>
    /// Gets a custom property value
    /// </summary>
    public string? GetProperty(string name)
    {
        return CustomProperties.TryGetValue(name, out var value) ? value : null;
    }
}

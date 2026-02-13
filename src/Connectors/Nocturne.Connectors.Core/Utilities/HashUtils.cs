using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Connectors.Core.Utilities;

/// <summary>
///     Shared hashing utilities for connectors.
///     Used for generating unique IDs, hashing API secrets, and account identification.
/// </summary>
public static class HashUtils
{
    /// <summary>
    ///     Computes SHA256 hash and returns as lowercase hex string.
    ///     Used for secure hashing of sensitive identifiers.
    /// </summary>
    /// <param name="input">The string to hash</param>
    /// <returns>Lowercase hex string of the SHA256 hash</returns>
    public static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    ///     Computes SHA1 hash and returns as lowercase hex string.
    ///     Used for Nightscout API secret hashing and treatment ID generation.
    /// </summary>
    /// <param name="input">The string to hash</param>
    /// <returns>Lowercase hex string of the SHA1 hash</returns>
    public static string Sha1Hex(string input)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    ///     Generates a unique treatment/entry ID based on event type, timestamp, and additional data.
    ///     Uses SHA1 to create a consistent, reproducible ID for deduplication.
    /// </summary>
    /// <param name="source">The connector source identifier (e.g., "glooko", "dexcom")</param>
    /// <param name="eventType">The type of event (e.g., "Meal Bolus", "Correction Bolus")</param>
    /// <param name="timestamp">The timestamp of the event</param>
    /// <param name="additionalData">Optional additional data for uniqueness (e.g., "carbs:50_insulin:5")</param>
    /// <returns>A unique hex ID for the treatment</returns>
    public static string GenerateEventId(string source, string eventType, DateTime timestamp,
        string? additionalData = null)
    {
        var dataToHash = $"{source}_{eventType}_{timestamp.Ticks}_{additionalData ?? ""}";
        return Sha1Hex(dataToHash);
    }
}
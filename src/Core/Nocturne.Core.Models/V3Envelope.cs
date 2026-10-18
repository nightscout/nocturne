namespace Nocturne.Core.Models;

/// <summary>
/// The Nightscout v3 response wrapper: <c>{ status, result }</c>.
/// </summary>
/// <typeparam name="T">Type of the wrapped result</typeparam>
/// <seealso cref="V3VersionResult"/>
/// <seealso cref="V3StatusResponse"/>
public class V3Envelope<T>
{
    /// <summary>
    /// HTTP-style status code carried in the body
    /// </summary>
    public int Status { get; set; } = 200;

    /// <summary>
    /// The wrapped payload
    /// </summary>
    public T Result { get; set; } = default!;
}

/// <summary>
/// Result of <c>/api/v3/version</c>.
/// </summary>
/// <seealso cref="V3Envelope{T}"/>
/// <seealso cref="V3StatusResponse"/>
public class V3VersionResult
{
    /// <summary>
    /// Nocturne release version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// V3 API level, <see cref="Nocturne.Core.Constants.ApplicationConstants.NightscoutApi.V3Version"/>
    /// </summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>
    /// Server date as Unix epoch milliseconds
    /// </summary>
    public long SrvDate { get; set; }

    public StorageInfo Storage { get; set; } = new();
}

/// <summary>
/// Result of <c>/api/v3/lastModified</c>.
/// </summary>
/// <seealso cref="V3Envelope{T}"/>
/// <seealso cref="LastModifiedResponse"/>
public class V3LastModifiedResult
{
    /// <summary>
    /// Server date as Unix epoch milliseconds
    /// </summary>
    public long SrvDate { get; set; }

    /// <summary>
    /// Last modified Unix epoch milliseconds by lowercase collection key
    /// </summary>
    public Dictionary<string, long> Collections { get; set; } = new();
}

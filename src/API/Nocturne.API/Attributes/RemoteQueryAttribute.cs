namespace Nocturne.API.Attributes;

/// <summary>
/// Marks an endpoint for remote function generation as a query (read operation).
/// Queries are cached and can be refreshed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RemoteQueryAttribute : Attribute
{
    /// <summary>
    /// If true, skip generating a remote function for this endpoint.
    /// The endpoint remains accessible via the raw ApiClient.
    /// </summary>
    public bool Skip { get; set; } = false;
}

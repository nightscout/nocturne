namespace Nocturne.API.Attributes;

/// <summary>
/// Marks an endpoint for remote function generation as a command (write operation).
/// Commands can specify which queries to invalidate after execution.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RemoteCommandAttribute : Attribute
{
    /// <summary>
    /// Operation names of queries to invalidate after this command succeeds.
    /// Use short names for same-domain (e.g., "GetNotes") or full operationId
    /// for cross-domain (e.g., "Trackers_GetActiveInstances").
    /// </summary>
    public string[] Invalidates { get; set; } = [];

    /// <summary>
    /// If true, skip generating a remote function for this endpoint.
    /// The endpoint remains accessible via the raw ApiClient.
    /// </summary>
    public bool Skip { get; set; } = false;
}

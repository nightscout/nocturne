namespace Nocturne.Core.Contracts.Audit;

/// <summary>
/// An <see cref="IAuditContext"/> a background job can turn into system attribution for the rest
/// of its own DI scope, so every write the scope makes, on any context, counts as
/// <see cref="AuditContextExtensions.IsSystemMutation"/>.
/// </summary>
/// <remarks>
/// For a scope the job opened itself. A scope that outlives the job, such as a request's, is
/// attributed for a stretch and restored instead, through <c>SystemAuditScope</c>.
/// </remarks>
public interface ISystemAttributable
{
    /// <summary>
    /// Clears the actor fields and marks the context as the system's, under
    /// <paramref name="endpoint"/> (e.g. <c>"service:deduplication"</c>).
    /// </summary>
    void AttributeToSystem(string endpoint);
}

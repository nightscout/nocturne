namespace Nocturne.API.Services;

/// <summary>
/// A contribution the upstream repository will not take as submitted — a
/// stale catalog, a disallowed path, content identical to what is published.
/// Distinct from a transport failure: the caller surfaces it as 422 and the
/// message is shown to the contributor.
/// </summary>
public class ContributionRejectedException(string message) : Exception(message);

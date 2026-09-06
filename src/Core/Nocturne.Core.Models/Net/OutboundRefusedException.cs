namespace Nocturne.Core.Models.Net;

/// <summary>
/// An outbound request the server declined to make: the host could not be found, or it resolves to
/// an address a user-supplied target may not reach.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> already names the cause for the person who supplied the URL, and
/// says more than the transport could — "could not be found" and "resolves to a forbidden address"
/// are the same <see cref="HttpRequestException"/> to a caller that only sees the base type. A
/// caller translating fetch failures into its own wording must pass this one through rather than
/// substitute a generic "could not connect".
/// </remarks>
public sealed class OutboundRefusedException(string message) : HttpRequestException(message);

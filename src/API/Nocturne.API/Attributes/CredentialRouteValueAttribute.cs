namespace Nocturne.API.Attributes;

/// <summary>
/// Marks a route parameter whose value is a credential, so telemetry never records it.
/// </summary>
/// <seealso cref="Configuration.CredentialPathRedactor"/>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class CredentialRouteValueAttribute : Attribute;

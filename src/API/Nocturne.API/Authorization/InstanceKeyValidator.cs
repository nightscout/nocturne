using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;

namespace Nocturne.API.Authorization;

/// <summary>
/// Classification of a request's instance-key credential.
/// </summary>
public enum InstanceKeyRequestKind
{
    /// <summary>No instance-key credential is present — the header or the service marker is missing.</summary>
    Absent,

    /// <summary>A credential was presented but no instance key is configured on the server.</summary>
    NotConfigured,

    /// <summary>A credential was presented but does not match the configured instance key.</summary>
    Invalid,

    /// <summary>A valid instance-key service request from a trusted in-cluster caller.</summary>
    Valid,
}

/// <summary>
/// Validates the shared instance key used for service-to-service authentication.
/// A valid request carries both the <see cref="ServiceNames.Headers.InstanceKey"/>
/// header (SHA-256 hash of the key) and an <see cref="ServiceNames.Headers.InstanceService"/>
/// marker.
/// </summary>
/// <remarks>
/// Shared by <see cref="Middleware.Handlers.InstanceKeyHandler"/> (which authenticates the
/// request) and <see cref="Middleware.TenantSetupMiddleware"/> (which lets such requests
/// bypass the setup gate), so the validation rules — including the requirement of a service
/// marker — live in exactly one place.
/// </remarks>
public interface IInstanceKeyValidator
{
    /// <summary>
    /// Classifies the request's instance-key credential without mutating the request.
    /// </summary>
    InstanceKeyRequestKind Classify(HttpContext context);
}

/// <inheritdoc />
public class InstanceKeyValidator : IInstanceKeyValidator
{
    private readonly string _instanceKeyHash;

    public InstanceKeyValidator(IConfiguration configuration)
    {
        var instanceKey =
            configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
            ?? configuration[ServiceNames.ConfigKeys.InstanceKey]
            ?? "";
        _instanceKeyHash = !string.IsNullOrEmpty(instanceKey) ? HashUtils.Sha256Hex(instanceKey) : "";
    }

    /// <inheritdoc />
    public InstanceKeyRequestKind Classify(HttpContext context)
    {
        var header = context.Request.Headers[ServiceNames.Headers.InstanceKey].FirstOrDefault();
        if (string.IsNullOrEmpty(header))
            return InstanceKeyRequestKind.Absent;

        // Require an explicit service marker. A bare instance key with no service
        // declaration is treated as "not an intended service credential" so that an
        // instance key accidentally forwarded onto an anonymous browser request
        // (e.g. by the SSR proxy) does not elevate that request or slip past the
        // setup gate.
        var serviceMarker = context.Request.Headers[ServiceNames.Headers.InstanceService].FirstOrDefault();
        if (string.IsNullOrEmpty(serviceMarker))
            return InstanceKeyRequestKind.Absent;

        if (string.IsNullOrEmpty(_instanceKeyHash))
            return InstanceKeyRequestKind.NotConfigured;

        return string.Equals(header, _instanceKeyHash, StringComparison.OrdinalIgnoreCase)
            ? InstanceKeyRequestKind.Valid
            : InstanceKeyRequestKind.Invalid;
    }
}

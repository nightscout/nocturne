namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Links a <see cref="Subject"/> to an external OIDC identity, enabling federated login.
/// A single subject may have multiple identities from different <see cref="OidcProvider"/>s.
/// </summary>
/// <seealso cref="Subject"/>
/// <seealso cref="OidcProvider"/>
public class SubjectOidcIdentity
{
    public Guid Id { get; set; }

    /// <summary>The <see cref="Subject"/> this identity is linked to.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>The <see cref="OidcProvider"/> that issued this identity.</summary>
    public Guid ProviderId { get; set; }

    /// <summary>Display name of the OIDC provider (denormalized for UI).</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Icon identifier or URL for the provider (denormalized for UI).</summary>
    public string? ProviderIcon { get; set; }

    /// <summary>Button color for the provider in the login UI (denormalized for UI).</summary>
    public string? ProviderButtonColor { get; set; }

    /// <summary>The <c>sub</c> claim value from the OIDC provider's ID token.</summary>
    public string OidcSubjectId { get; set; } = string.Empty;

    /// <summary>Issuer URL from the OIDC provider's discovery document.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Email address from the OIDC provider's claims, if available.</summary>
    public string? Email { get; set; }

    /// <summary>When this identity was first linked to the subject.</summary>
    public DateTime LinkedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// Outcome of attempting to link an OIDC identity to a <see cref="Subject"/>.
/// </summary>
/// <seealso cref="SubjectOidcIdentity"/>
public enum OidcLinkOutcome
{
    /// <summary>A new identity link was created successfully.</summary>
    Created,

    /// <summary>This OIDC identity is already linked to the requesting subject.</summary>
    AlreadyLinkedToSelf,

    /// <summary>This OIDC identity is already linked to a different subject.</summary>
    AlreadyLinkedToOther,
}

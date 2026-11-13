namespace Nocturne.Core.Models.Identity;

/// <summary>Instance-wide policies a platform admin sets.</summary>
/// <param name="AllowOwnerSlugChange">
/// Whether tenant owners may change their tenant's subdomain themselves. Platform admins can change
/// any tenant's subdomain either way.
/// </param>
public sealed record PlatformPolicy(bool AllowOwnerSlugChange = false);

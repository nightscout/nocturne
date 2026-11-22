namespace Nocturne.Core.Models.Identity;

/// <summary>Instance-wide policies a platform admin sets.</summary>
/// <param name="AllowOwnerSlugChange">
/// Whether tenant owners may change their tenant's subdomain themselves. Platform admins can change
/// any tenant's subdomain either way.
/// </param>
/// <param name="RestrictTeamCreationToAdmins">
/// Whether only platform admins may create a team. Anyone signed in may when it is off.
/// </param>
public sealed record PlatformPolicy(bool AllowOwnerSlugChange = false, bool RestrictTeamCreationToAdmins = false);

/// <summary>A change to <see cref="PlatformPolicy"/>. A switch left null keeps its saved value.</summary>
public sealed record PlatformPolicyUpdate(bool? AllowOwnerSlugChange = null, bool? RestrictTeamCreationToAdmins = null);

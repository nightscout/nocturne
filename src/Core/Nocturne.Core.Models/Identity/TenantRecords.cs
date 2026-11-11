using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Identity;

/// <summary>A row of <c>tenants</c>, with its demo configuration when it has one.</summary>
/// <param name="ShareTokenHash">The digest of the live public share token; null when the link is off.</param>
/// <param name="ShareTokenEncrypted">
/// The same token again, as ciphertext under the instance key, so the owner can be shown the link
/// they are already handing out instead of rotating it, which would break it for everyone holding
/// it. Nothing authenticates against it; <paramref name="ShareTokenHash"/> remains the only
/// resolution path. The single site for why a live link can still be unshowable: the link was
/// minted before the ciphertext was kept, so nothing was kept; the instance has no key, so nothing
/// could be kept; or the key changed since, so what was kept no longer decrypts. Only the third is
/// invisible in the columns and is knowable only by attempting the decrypt. All three fall back
/// to "regenerate to see a link", and leave the link itself working, since resolution reads the digest.
/// </param>
public sealed record TenantRecord(
    Guid Id,
    string Slug,
    string DisplayName,
    bool IsActive,
    bool IsDemo,
    bool AllowAccessRequests,
    bool AllowPublicDocs,
    DateTime? OnboardingCompletedAt,
    DateTime? SetupHubEnrolledAt,
    string? SetupStripDismissedRevision,
    PatientRelationship? PatientRelationship,
    DateTime? HandedOverAt,
    string? DefaultGlucoseUnits,
    DateTime? LastReadingAt,
    string? ShareTokenHash,
    string? ShareTokenEncrypted,
    DateTime? ShareTokenSetAt,
    DateTime? ShareLastAccessedAt,
    DateTime SysCreatedAt,
    DateTime SysUpdatedAt,
    DemoTenantConfig? DemoConfig);

/// <summary>The demo tenant's operational state, which survives its resets.</summary>
public sealed record DemoTenantConfig(
    DateTime? NextResetAt,
    DateTime? LastResetAt,
    string AccessMode,
    int BackfillDays,
    int IntervalMinutes,
    int ResetIntervalMinutes);

/// <summary>
/// A tenant to store. <paramref name="Id"/> is given when a reset re-inserts a tenant under its old id;
/// <paramref name="DemoConfig"/> stores the demo configuration row with it.
/// </summary>
public sealed record NewTenant(
    string Slug,
    string DisplayName,
    Guid? Id = null,
    bool IsActive = true,
    bool IsDemo = false,
    bool AllowAccessRequests = true,
    bool AllowPublicDocs = false,
    DateTime? OnboardingCompletedAt = null,
    string? ShareTokenHash = null,
    string? ShareTokenEncrypted = null,
    DateTime? ShareTokenSetAt = null,
    DemoTenantConfig? DemoConfig = null);

/// <summary>Changes to a tenant's columns; a null member leaves its column as it is.</summary>
public sealed record TenantEdit(
    string? DisplayName = null,
    bool? IsActive = null,
    bool? IsDemo = null,
    bool? AllowAccessRequests = null,
    bool? AllowPublicDocs = null,
    PatientRelationship? PatientRelationship = null,
    DateTime? HandedOverAt = null,
    string? DefaultGlucoseUnits = null,
    DateTime? OnboardingCompletedAt = null,
    DateTime? SetupHubEnrolledAt = null,
    string? SetupStripDismissedRevision = null,
    DemoConfigEdit? DemoConfig = null);

/// <summary>
/// Changes to a tenant's demo configuration, stored with the tenant edit that carries it; the row
/// is created with its defaults when the tenant has none, and a null member leaves its column as it is.
/// </summary>
public sealed record DemoConfigEdit(DateTime? NextResetAt = null, DateTime? LastResetAt = null);

/// <summary>The public share token's three columns, written together; all null switches the link off.</summary>
public sealed record ShareTokenState(string? Hash, string? Encrypted, DateTime? SetAt)
{
    public static ShareTokenState Off { get; } = new(null, null, null);
}

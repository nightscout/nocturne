using System.Text.Json;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Service for managing authentication <see cref="Subject"/>s — creation, lookup, role assignment
/// and the rules around a subject's sign-in factors — over <see cref="ISubjectRepository"/>.
/// All audit-significant operations are forwarded to <see cref="IAuthAuditService"/>.
/// </summary>
/// <seealso cref="ISubjectService"/>
/// <seealso cref="IAuthAuditService"/>
/// <seealso cref="OAuthTokenService"/>
/// <seealso cref="RoleService"/>
public class SubjectService : ISubjectService
{
    private readonly ISubjectRepository _subjects;
    private readonly IAuthAuditService _auditService;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly ILogger<SubjectService> _logger;

    /// <param name="auditService">Service for recording audit events on authentication actions.</param>
    /// <param name="recoveryCodeService">Service for counting the subject's unused recovery codes.</param>
    public SubjectService(
        ISubjectRepository subjects,
        IAuthAuditService auditService,
        IRecoveryCodeService recoveryCodeService,
        ILogger<SubjectService> logger
    )
    {
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _auditService = auditService;
        _recoveryCodeService = recoveryCodeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Subject?> GetSubjectByIdAsync(Guid subjectId)
    {
        var record = await _subjects.GetAsync(subjectId, CancellationToken.None);
        return record is null ? null : MapToModel(record);
    }

    /// <inheritdoc />
    public async Task<Subject> FindOrCreateFromOidcAsync(
        Guid providerId,
        string oidcSubjectId,
        string issuer,
        string? email = null,
        bool emailVerified = false,
        string? name = null,
        IEnumerable<string>? defaultRoles = null
    )
    {
        var verifiedEmail = emailVerified && !string.IsNullOrEmpty(email) ? email : null;
        var now = DateTime.UtcNow;

        var match = await _subjects.FindByOidcIdentityAsync(oidcSubjectId, issuer, CancellationToken.None);
        if (match is not null)
        {
            var existing = match.Subject;
            await _subjects.TouchOidcIdentityAsync(match.Identity.Id, now, email: null, CancellationToken.None);

            var newEmail = verifiedEmail is not null && existing.Email != verifiedEmail ? verifiedEmail : null;
            var newName = !string.IsNullOrEmpty(name) && existing.Name != name ? name : null;
            if (newEmail is not null || newName is not null)
            {
                await _subjects.UpdateAsync(existing.Id, new SubjectEdit(Name: newName, Email: newEmail), CancellationToken.None);
                existing = existing with { Email = newEmail ?? existing.Email, Name = newName ?? existing.Name };
            }

            _logger.LogDebug(
                "Found existing subject {SubjectId} for OIDC identity {OidcSubjectId}",
                existing.Id,
                oidcSubjectId
            );
            return MapToModel(existing);
        }

        var subjectId = await _subjects.CreateAsync(
            new NewSubject(
                Name: name ?? email ?? oidcSubjectId,
                Email: verifiedEmail,
                RoleNames: defaultRoles?.ToList(),
                OidcIdentity: new NewOidcLink(providerId, oidcSubjectId, issuer, email, now)),
            CancellationToken.None);

        _logger.LogInformation(
            "Created new subject {SubjectId} for OIDC identity {OidcSubjectId}",
            subjectId,
            oidcSubjectId
        );
        return MapToModel((await _subjects.GetAsync(subjectId, CancellationToken.None))!);
    }

    /// <inheritdoc />
    public async Task<SubjectCreationResult> CreateSubjectAsync(Subject subject)
    {
        var subjectId = await _subjects.CreateAsync(
            new NewSubject(
                Name: subject.Name,
                Email: subject.Email,
                Notes: subject.Notes,
                IsActive: subject.IsActive,
                Id: subject.Id == Guid.Empty ? null : subject.Id,
                RoleNames: subject.Roles.Select(role => role.Name).ToList()),
            CancellationToken.None);

        var created = (await _subjects.GetAsync(subjectId, CancellationToken.None))!;

        _logger.LogInformation("Created subject {SubjectId} ({Name})", created.Id, created.Name);

        await _auditService.LogAsync(AuthAuditEventType.SubjectCreated, created.Id, success: true,
            detailsJson: JsonSerializer.Serialize(new { name = created.Name }));

        return new SubjectCreationResult
        {
            Subject = MapToModel(created),
        };
    }

    /// <inheritdoc />
    public async Task<Subject?> UpdateSubjectAsync(Subject subject)
    {
        var updated = await _subjects.UpdateProfileAsync(
            subject.Id,
            new SubjectProfile(subject.Name, subject.Email, subject.Notes),
            CancellationToken.None);

        if (updated is null)
        {
            return null;
        }

        _logger.LogInformation("Updated subject {SubjectId}", updated.Id);

        await _auditService.LogAsync(AuthAuditEventType.SubjectUpdated, updated.Id, success: true,
            detailsJson: JsonSerializer.Serialize(new { name = updated.Name }));

        return MapToModel(updated);
    }

    /// <inheritdoc />
    public async Task<SubjectChange> DeleteSubjectAsync(Guid subjectId)
    {
        var subject = await _subjects.GetAsync(subjectId, CancellationToken.None);
        if (subject == null)
        {
            return new SubjectChange.NotFound();
        }

        if (subject.IsSystemSubject)
        {
            _logger.LogWarning("Attempted to delete system subject {SubjectId} ({Name})", subjectId, subject.Name);
            return new SubjectChange.NotFound();
        }

        var change = await _subjects.DeleteAsync(subjectId, CancellationToken.None);
        if (change is not SubjectChange.Changed)
        {
            return change;
        }

        _logger.LogInformation("Deleted subject {SubjectId}", subjectId);

        await _auditService.LogAsync(AuthAuditEventType.SubjectDeleted, subjectId, success: true);

        return change;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateSubjectAsync(Guid subjectId) =>
        await _subjects.UpdateAsync(subjectId, new SubjectEdit(IsActive: true), CancellationToken.None)
            is SubjectChange.Changed;

    /// <inheritdoc />
    public Task<SubjectChange> DeactivateSubjectAsync(Guid subjectId) =>
        _subjects.UpdateAsync(subjectId, new SubjectEdit(IsActive: false), CancellationToken.None);

    /// <inheritdoc />
    public async Task<List<string>> GetSubjectRolesAsync(Guid subjectId) =>
        (await _subjects.GetRolesAsync(subjectId, CancellationToken.None)).Select(role => role.Name).ToList();

    /// <inheritdoc />
    public async Task<List<string>> GetSubjectPermissionsAsync(Guid subjectId)
    {
        var roles = await _subjects.GetRolesAsync(subjectId, CancellationToken.None);

        var permissions = new HashSet<string>();
        foreach (var role in roles)
        {
            foreach (var permission in role.Permissions)
            {
                permissions.Add(permission);
            }
        }

        return permissions.ToList();
    }

    /// <inheritdoc />
    public async Task<bool> AssignRoleAsync(
        Guid subjectId,
        string roleName,
        Guid? assignedBy = null
    )
    {
        var outcome = await _subjects.AssignRoleAsync(subjectId, roleName, assignedBy, DateTime.UtcNow, CancellationToken.None);
        switch (outcome)
        {
            case RoleAssignment.RoleNotFound:
                _logger.LogWarning(
                    "Role {RoleName} not found when assigning to subject {SubjectId}",
                    roleName,
                    subjectId
                );
                return false;
            case RoleAssignment.AlreadyAssigned:
                return false;
        }

        _logger.LogInformation(
            "Assigned role {RoleName} to subject {SubjectId}",
            roleName,
            subjectId
        );

        await _auditService.LogAsync(AuthAuditEventType.RoleAssigned, subjectId, success: true,
            detailsJson: JsonSerializer.Serialize(new { role = roleName }),
            actor: assignedBy is null ? null : new AuthAuditActor(assignedBy, null));

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleAsync(Guid subjectId, string roleName)
    {
        if (!await _subjects.RemoveRoleAsync(subjectId, roleName, CancellationToken.None))
        {
            return false;
        }

        _logger.LogInformation(
            "Removed role {RoleName} from subject {SubjectId}",
            roleName,
            subjectId
        );

        await _auditService.LogAsync(AuthAuditEventType.RoleRemoved, subjectId, success: true,
            detailsJson: JsonSerializer.Serialize(new { role = roleName }));

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HasPermissionAsync(Guid subjectId, string permission)
    {
        var permissions = await GetSubjectPermissionsAsync(subjectId);

        // Check for admin permission
        if (permissions.Contains("*"))
            return true;

        // Check exact match
        if (permissions.Contains(permission))
            return true;

        // Check hierarchical wildcards
        var parts = permission.Split(':');
        for (int i = 1; i <= parts.Length; i++)
        {
            var wildcardPermission = string.Join(":", parts.Take(i)) + ":*";
            if (permissions.Contains(wildcardPermission))
                return true;
        }

        // Check *:*:action pattern
        if (parts.Length >= 3)
        {
            var actionWildcard = $"*:*:{parts[^1]}";
            if (permissions.Contains(actionWildcard))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<Subject?> InitializePublicSubjectAsync()
    {
        var existing = await _subjects.FindSystemSubjectAsync(PublicSubjectFilter.PublicSubjectName, CancellationToken.None);
        if (existing != null)
        {
            _logger.LogDebug("Public subject already exists");
            return MapToModel(existing);
        }

        var subjectId = await _subjects.CreateAsync(
            new NewSubject(
                Name: PublicSubjectFilter.PublicSubjectName,
                Notes: "Represents unauthenticated access. Assign roles to control what the public can see.",
                IsSystemSubject: true),
            CancellationToken.None);

        await AssignRoleAsync(subjectId, "public");

        _logger.LogInformation("Created system Public subject for unauthenticated access");

        return MapToModel((await _subjects.GetAsync(subjectId, CancellationToken.None))!);
    }

    /// <inheritdoc />
    public Task UpdateLastLoginAsync(Guid subjectId) =>
        _subjects.UpdateAsync(subjectId, new SubjectEdit(LastLoginAt: DateTime.UtcNow), CancellationToken.None);

    /// <inheritdoc />
    public Task<IReadOnlyList<SubjectOidcIdentity>> GetLinkedOidcIdentitiesAsync(Guid subjectId) =>
        _subjects.GetOidcIdentitiesAsync(subjectId, CancellationToken.None);

    /// <inheritdoc />
    public async Task<(OidcLinkOutcome Outcome, Guid? IdentityId)> AttachOidcIdentityAsync(
        Guid subjectId, Guid providerId, string oidcSubjectId, string issuer, string? email)
    {
        var existing = await _subjects.FindByOidcIdentityAsync(oidcSubjectId, issuer, CancellationToken.None);
        if (existing != null)
        {
            if (existing.Identity.SubjectId == subjectId)
            {
                await _subjects.TouchOidcIdentityAsync(existing.Identity.Id, DateTime.UtcNow, email, CancellationToken.None);
                return (OidcLinkOutcome.AlreadyLinkedToSelf, existing.Identity.Id);
            }
            return (OidcLinkOutcome.AlreadyLinkedToOther, null);
        }

        var identityId = await _subjects.AddOidcIdentityAsync(
            new NewSubjectOidcIdentity(subjectId, providerId, oidcSubjectId, issuer, email, DateTime.UtcNow),
            CancellationToken.None);
        return (OidcLinkOutcome.Created, identityId);
    }

    /// <inheritdoc />
    public async Task<SubjectOidcIdentity?> GetMostRecentlyUsedIdentityAsync(Guid subjectId) =>
        (await _subjects.GetOidcIdentitiesAsync(subjectId, CancellationToken.None))
            .OrderByDescending(identity => identity.LastUsedAt ?? identity.LinkedAt)
            .FirstOrDefault();

    /// <inheritdoc />
    public Task<FactorRemovalResult> TryRemoveOidcIdentityAsync(Guid subjectId, Guid identityId) =>
        _subjects.TryRemoveFactorAsync(subjectId, new SignInFactor.OidcIdentity(identityId), CancellationToken.None);

    /// <inheritdoc />
    public Task<FactorRemovalResult> TryRemovePasskeyCredentialAsync(Guid subjectId, Guid credentialId) =>
        _subjects.TryRemoveFactorAsync(subjectId, new SignInFactor.Passkey(credentialId), CancellationToken.None);

    /// <inheritdoc />
    public Task<int> CountPrimaryAuthFactorsAsync(Guid subjectId) =>
        _subjects.CountPrimaryAuthFactorsAsync(subjectId, CancellationToken.None);

    /// <inheritdoc />
    public async Task<bool> HasSingleSignInMethodAsync(Guid subjectId)
    {
        if (await CountPrimaryAuthFactorsAsync(subjectId) != 1)
        {
            return false;
        }

        return await _recoveryCodeService.GetRemainingCountAsync(subjectId) == 0;
    }

    /// <inheritdoc />
    public Task UpdateOidcIdentityLastUsedAsync(Guid identityId) =>
        _subjects.TouchOidcIdentityAsync(identityId, DateTime.UtcNow, email: null, CancellationToken.None);

    private static Subject MapToModel(SubjectRecord record)
    {
        var subject = new Subject
        {
            Id = record.Id,
            Name = record.Name,
            Email = record.Email,
            IsActive = record.IsActive,
            IsSystemSubject = record.IsSystemSubject,
            IsPlatformAdmin = record.IsPlatformAdmin,
            CreatedAt = record.CreatedAt,
            LastLoginAt = record.LastLoginAt,
            Notes = record.Notes,
            PreferredLanguage = record.PreferredLanguage,
            Preferences = record.Preferences,
            AvatarUrl = record.AvatarUrl,
            Roles = record.Roles.ToList(),
            Permissions = record.Roles.SelectMany(role => role.Permissions).Distinct().ToList(),
        };

        // Every subject is a person: a device or service credential is a direct grant, not a
        // subject, so nothing here can distinguish one.
        subject.Type = SubjectType.User;
        return subject;
    }
}

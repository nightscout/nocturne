using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// The rule for a subject consenting to a client: the active grant the subject already holds for it
/// takes the union of its scopes and the new ones, and a subject with none gets a new grant. A grant
/// revoked while the consent reads it is not merged into: the consent creates a new one.
/// </summary>
/// <remarks>
/// A writer on the caller's <see cref="DbUnitOfWork"/>, so a consent can commit with the write that
/// depends on it and roll back with it. It is called inside the work of
/// <see cref="DbUnitOfWork.CommitAsync(Func{CancellationToken, Task}, CancellationToken)"/> and saves
/// the grant there, so a statement that follows in the same transaction can refer to its id. The
/// owner logs the consent through <see cref="LogCommitted"/> once the commit has landed.
/// <para>
/// A lock on the subject and client (<see cref="IOAuthGrantRepository.LockConsentAsync"/>) is held from
/// before the read to the commit, so a consent that waits on another finds the grant it wrote and
/// merges into it. <c>ix_oauth_grants_tenant_client_subject_active</c> is the backstop: no path that
/// takes the lock can reach it.
/// </para>
/// <para>
/// The same call takes the subject's membership lock and reads the membership under it, so a consent
/// from a removed member writes nothing (<see cref="IOAuthGrantRepository.LockConsentAsync"/>).
/// </para>
/// </remarks>
public sealed class GrantConsentService
{
    private readonly ILogger<GrantConsentService> _logger;

    public GrantConsentService(ILogger<GrantConsentService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates the grant, or merges <paramref name="scopes"/> into the active one, on
    /// <paramref name="unit"/>.
    /// </summary>
    /// <param name="unit">The caller's unit of work, inside the work of its commit.</param>
    /// <param name="clientEntityId">The internal id of the client consented to.</param>
    /// <param name="subjectId">The subject granting access.</param>
    /// <param name="scopes">The scopes consented to.</param>
    /// <param name="limitTo24Hours">The latest consent sets whether tokens read only the last 24 hours.</param>
    /// <param name="approvedByDeviceCode">Whether a device-code approval is consenting. Once set it stays set.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The consent as written; null when the subject holds no membership of the tenant, so nothing was written.</returns>
    public async Task<StagedGrantConsent?> StageAsync(
        DbUnitOfWork unit,
        Guid clientEntityId,
        Guid subjectId,
        IEnumerable<string> scopes,
        bool limitTo24Hours = false,
        bool approvedByDeviceCode = false,
        CancellationToken ct = default)
    {
        var grants = new OAuthGrantRepository(unit);
        if (!await grants.LockConsentAsync(clientEntityId, subjectId, ct))
            return null;

        var existingGrant = await grants.FindActiveAsync(clientEntityId, subjectId, ct);

        if (existingGrant != null)
        {
            var updated = await grants.MergeConsentAsync(
                existingGrant.Id, scopes, limitTo24Hours, approvedByDeviceCode, ct);
            if (updated != null)
            {
                await unit.Context.SaveChangesAsync(ct);
                return new StagedGrantConsent(OAuthGrantMapper.ToInfo(updated), Created: false);
            }
        }

        var grantId = await grants.CreateAsync(
            new NewOAuthGrant(
                clientEntityId,
                subjectId,
                OAuthGrantTypes.App,
                scopes.Distinct().OrderBy(s => s).ToList(),
                Label: null,
                TokenHash: null,
                LegacySecretHash: null,
                ExpiresAt: null,
                limitTo24Hours,
                approvedByDeviceCode),
            ct);
        await unit.Context.SaveChangesAsync(ct);
        var grant = await grants.GetAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Grant {grantId} is not readable after its save.");

        return new StagedGrantConsent(OAuthGrantMapper.ToInfo(grant), Created: true);
    }

    /// <summary>Writes the audit line of a consent whose unit has committed.</summary>
    public void LogCommitted(StagedGrantConsent consent)
    {
        var grant = consent.Grant;
        _logger.LogInformation(
            "OAuthAudit: {Event} grant_id={GrantId} subject_id={SubjectId} client_entity_id={ClientEntityId} scopes={Scopes}",
            consent.Created ? "grant_created" : "grant_updated",
            grant.Id,
            grant.SubjectId,
            grant.ClientEntityId,
            string.Join(" ", grant.Scopes));
    }
}

/// <summary>A consent written on a unit: the grant it left, and whether the consent created it.</summary>
public sealed record StagedGrantConsent(OAuthGrantInfo Grant, bool Created);

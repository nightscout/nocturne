using System.Text.Json;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Stages an <c>auth_audit_log</c> row on the context that stages the write it records, so one save
/// commits both and a write that fails leaves no row.
/// </summary>
/// <remarks>
/// The actor is the <see cref="MemberAccessSource"/> when the writer passes one, else the scope's
/// <see cref="IAuditContext"/> named as <see cref="AuthAuditActor.From(IAuditContext)"/> names it, else
/// the system when the scope has no actor or is system-attributed. A scope with no caller at all, and
/// no source, is recorded as <c>anonymous</c>, never as a credential.
/// </remarks>
internal static class AuthAuditRows
{
    internal const string System = "system";
    internal const string Anonymous = "anonymous";
    private const int CredentialLength = 64;

    public static void Stage(
        NocturneDbContext db, string eventType, Guid? tenantId, Guid? teamId, Guid? targetSubjectId,
        IReadOnlyDictionary<string, object?> details, MemberAccessSource? source = null)
    {
        var audit = db.AuditContext;
        var actor = ActorOf(audit, source);

        db.AuthAuditLog.Add(new AuthAuditLogEntity
        {
            Id = AuditRowIds.Next(db),
            EventType = eventType,
            TargetSubjectId = targetSubjectId,
            ActorSubjectId = actor.SubjectId,
            ActorCredential = actor.Credential is { Length: > CredentialLength } credential ? credential[..CredentialLength] : actor.Credential,
            TenantId = tenantId,
            TeamId = teamId,
            Success = true,
            IpAddress = audit?.IpAddress,
            UserAgent = audit?.UserAgent,
            TraceId = audit?.TraceId,
            DetailsJson = JsonSerializer.Serialize(details),
        });
    }

    private static AuthAuditActor ActorOf(IAuditContext? audit, MemberAccessSource? source)
    {
        if (source is not null)
            return new AuthAuditActor(source.ActorSubjectId, source.ActorSubjectId is null ? System : null);
        if (audit is null or { IsSystem: true })
            return new AuthAuditActor(null, audit?.AuthType ?? System);
        if (audit is { SubjectId: null, CredentialFingerprint: null, TokenId: null }
            && audit.AuthType is null or nameof(AuthType.None))
            return new AuthAuditActor(null, Anonymous);
        return AuthAuditActor.From(audit);
    }
}

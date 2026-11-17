using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>An <see cref="IAuditContext"/> for a test that checks who a write is attributed to.</summary>
public sealed class TestAuditActor : IAuditContext
{
    public Guid? SubjectId { get; init; }
    public string? SubjectName => null;
    public string? AuthType { get; init; }
    public string? IpAddress { get; init; }
    public Guid? TokenId { get; init; }
    public string? CredentialFingerprint { get; init; }
    public string? UserAgent { get; init; }
    public string? TraceId { get; init; }
    public string? Endpoint => null;
    public bool IsSystem { get; init; }
}

public static class TestAuditActorExtensions
{
    /// <summary>
    /// Makes every context the test's factory hands out read <paramref name="actor"/> as the scope's
    /// audit actor, as the request scope does.
    /// </summary>
    public static T ActingAs<T>(this T context, IAuditContext? actor) where T : NocturneDbContext
    {
        context.ReadContext = new TenantReadContext(null, null, actor);
        return context;
    }
}

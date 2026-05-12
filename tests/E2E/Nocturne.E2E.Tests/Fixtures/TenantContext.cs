namespace Nocturne.E2E.Tests.Fixtures;

public sealed record TenantContext(
    Guid TenantId,
    string Slug,
    Guid SubjectId,
    string Username,
    string AccessToken,
    string RefreshToken);

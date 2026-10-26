using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>A provider-grant store that holds nothing, for tests that never reach Apple.</summary>
internal sealed class NoProviderGrants : IOidcProviderGrantService
{
    public Task KeepAsync(OidcProvider provider, string oidcSubjectId, string? refreshToken, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<HeldProviderGrant>> CaptureAsync(
        Guid subjectId, Guid? identityId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HeldProviderGrant>>([]);

    public Task RevokeAsync(IReadOnlyCollection<HeldProviderGrant> grants, CancellationToken ct = default) =>
        Task.CompletedTask;
}

using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>Maps a stored grant to what the management UI and token minting see.</summary>
internal static class OAuthGrantMapper
{
    public static OAuthGrantInfo ToInfo(OAuthGrantRecord grant)
    {
        return new OAuthGrantInfo
        {
            Id = grant.Id,
            TenantId = grant.TenantId,
            ClientEntityId = grant.ClientEntityId,
            ClientId = grant.Client?.ClientId ?? string.Empty,
            ClientDisplayName = grant.Client?.DisplayName,
            ClientUri = grant.Client?.ClientUri,
            LogoUri = grant.Client?.LogoUri,
            ClientIdentity = grant.ClientIdentity,
            SubjectId = grant.SubjectId,
            GrantType = grant.GrantType,
            Scopes = [.. grant.Scopes],
            Label = grant.Label,
            CreatedAt = grant.CreatedAt,
            LastUsedAt = grant.LastUsedAt,
            LastUsedIp = grant.LastUsedIp,
            LastUsedUserAgent = grant.LastUsedUserAgent,
            IsRevoked = grant.IsRevoked,
            ExpiresAt = grant.ExpiresAt,
            LimitTo24Hours = grant.LimitTo24Hours,
        };
    }
}

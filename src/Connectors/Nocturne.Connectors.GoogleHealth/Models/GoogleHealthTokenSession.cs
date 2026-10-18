namespace Nocturne.Connectors.GoogleHealth.Models;

public sealed record GoogleHealthTokenSession(
    string RefreshToken,
    string[] Scopes,
    string? AccessToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null);

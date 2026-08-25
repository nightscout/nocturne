using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Validates requested scopes during DCR against the canonical scope registry.
/// </summary>
public static class RegistrationScopeValidator
{
    /// <summary>
    /// Validates a space-delimited scope string from a DCR request against the
    /// canonical <see cref="Scope.ValidRequestScopes"/> registry.
    /// </summary>
    /// <param name="scopeString">
    /// A space-delimited list of requested scopes, or <see langword="null"/> / empty to request no specific scopes.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when all requested scopes are valid (or no scopes were requested);
    /// otherwise a list of the unrecognised scope values.
    /// </returns>
    public static List<string>? ValidateScopes(string? scopeString)
    {
        if (string.IsNullOrWhiteSpace(scopeString))
            return null; // No scopes requested — valid (will use defaults)

        var requested = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var unknown = requested.Where(s => !Scope.ValidRequestScopes.Contains(s)).ToList();

        return unknown.Count > 0 ? unknown : null;
    }
}

using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that handles follower access via the X-Acting-As header.
/// When a follower sends X-Acting-As: {ownerSubjectId}, this middleware validates
/// the follower grant and restricts scopes to the intersection of the user's
/// granted scopes and the follower grant's scopes.
/// Must run after AuthenticationMiddleware.
/// </summary>
public class FollowerAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FollowerAccessMiddleware> _logger;

    public FollowerAccessMiddleware(RequestDelegate next, ILogger<FollowerAccessMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var actingAsHeader = context.Request.Headers["X-Acting-As"].FirstOrDefault();

        if (string.IsNullOrEmpty(actingAsHeader))
        {
            await _next(context);
            return;
        }

        var authContext = context.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "access_denied",
                error_description = "Authentication required to use X-Acting-As header."
            });
            return;
        }

        if (!Guid.TryParse(actingAsHeader, out var ownerSubjectId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_request",
                error_description = "X-Acting-As header must be a valid UUID."
            });
            return;
        }

        // Acting as self - no-op
        if (ownerSubjectId == authContext.SubjectId)
        {
            await _next(context);
            return;
        }

        var followerSubjectId = authContext.SubjectId!.Value;

        // Resolve scoped services
        var grantService = context.RequestServices.GetRequiredService<IOAuthGrantService>();
        var subjectService = context.RequestServices.GetRequiredService<ISubjectService>();

        // Validate follower grant
        var grant = await grantService.GetActiveFollowerGrantAsync(ownerSubjectId, followerSubjectId);
        if (grant == null)
        {
            _logger.LogWarning(
                "Follower {FollowerId} attempted to act as {OwnerId} without an active grant",
                followerSubjectId, ownerSubjectId);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "access_denied",
                error_description = "No active follower grant for this data owner."
            });
            return;
        }

        // Look up owner display name
        var ownerSubject = await subjectService.GetSubjectByIdAsync(ownerSubjectId);

        // Set acting-as context
        authContext.ActingAsSubjectId = ownerSubjectId;
        authContext.ActingAsSubjectName = ownerSubject?.Name;

        // Restrict scopes: intersection of user's current scopes and grant's scopes
        var currentScopes = context.GetGrantedScopes();
        var grantScopes = OAuthScopes.Normalize(grant.Scopes);

        var restrictedScopes = grantScopes
            .Where(grantScope => OAuthScopes.SatisfiesScope(currentScopes, grantScope))
            .ToHashSet();

        context.Items["GrantedScopes"] = (IReadOnlySet<string>)restrictedScopes;

        // Rebuild permission trie from restricted scopes
        var restrictedPermissions = ScopeTranslator.ToPermissions(restrictedScopes);
        var permissionTrie = new PermissionTrie();
        permissionTrie.Add(restrictedPermissions);
        context.Items["PermissionTrie"] = permissionTrie;

        _logger.LogDebug(
            "Follower {FollowerId} acting as {OwnerId} with {ScopeCount} scopes",
            followerSubjectId, ownerSubjectId, restrictedScopes.Count);

        await _next(context);
    }
}

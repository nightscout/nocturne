using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// The auth pipeline parks its state on <see cref="HttpContext.Items"/>, an untyped bag whose keys
/// were spelled out at every read site. A typo read as "absent", and every consumer treats absent
/// as unauthenticated or unscoped — a silently open gate rather than a failure.
/// </summary>
[Trait("Category", "Unit")]
public class HttpContextAuthAccessorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    /// <summary>
    /// Two questions share the shape "does this caller hold X" and are NOT interchangeable.
    /// <see cref="HttpContextExtensions.HasScope"/> asks the granted scope set in the
    /// <c>glucose.read</c> vocabulary; <see cref="HttpContextExtensions.HasPermission"/> asks the
    /// legacy trie in the <c>api:entries:read</c> vocabulary. Seven controllers used to carry a
    /// private helper named for the second that did the first, so reaching for the extension of the
    /// same name would have silently changed which question was asked.
    /// </summary>
    [Fact]
    public void HasScope_and_HasPermission_ask_different_questions()
    {
        var context = new DefaultHttpContext();
        context.SetGrantedScopes(new HashSet<string> { Scope.MembersManage });

        context.HasScope(Scope.MembersManage).Should().BeTrue(
            "the scope set is what HasScope reads");

        context.HasPermission(Scope.MembersManage).Should().BeFalse(
            "HasPermission reads the legacy trie, which this request has none of — a caller who "
            + "swapped one for the other would silently change the gate");
    }

    [Fact]
    public void An_absent_scope_set_grants_nothing()
    {
        var context = new DefaultHttpContext();

        context.GetGrantedScopes().Should().BeEmpty();
        context.HasScope(Scope.GlucoseRead).Should().BeFalse();
    }

    [Fact]
    public void An_absent_permission_trie_grants_nothing()
    {
        var context = new DefaultHttpContext();

        context.GetPermissionTrie().Should().BeNull();
        context.HasPermission("api:entries:read").Should().BeFalse();
    }

    [Fact]
    public void Granted_scopes_round_trip_through_the_accessors()
    {
        var context = new DefaultHttpContext();
        var scopes = new HashSet<string> { Scope.GlucoseRead, Scope.TreatmentsReadWrite };

        context.SetGrantedScopes(scopes);

        context.GetGrantedScopes().Should().BeEquivalentTo(scopes);
        context.HasScope(Scope.TreatmentsRead).Should().BeTrue(
            "readwrite satisfies read, and HasScope defers to the vocabulary for that");
    }

    [Fact]
    public void The_tenant_context_round_trips_through_the_accessors()
    {
        var context = new DefaultHttpContext();
        var tenant = new TenantContext(TenantId, "acme", "Acme", IsActive: true, IsDemo: false);

        context.GetTenantContext().Should().BeNull("nothing has resolved a tenant yet");

        context.SetTenantContext(tenant);

        context.GetTenantContext().Should().Be(tenant);
    }

    [Fact]
    public void The_auth_context_round_trips_through_the_accessors()
    {
        var context = new DefaultHttpContext();

        context.GetAuthContext().Should().BeNull();
        context.IsAuthenticated().Should().BeFalse("an absent auth context is not a principal");

        context.SetAuthContext(new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie });

        context.GetAuthContext().Should().NotBeNull();
        context.IsAuthenticated().Should().BeTrue();
    }

    /// <summary>
    /// Share access is a bare <see langword="bool"/> in the bag, so a missing entry and a
    /// <see langword="false"/> entry have to read the same way round.
    /// </summary>
    [Fact]
    public void Share_access_is_false_until_it_is_set()
    {
        var context = new DefaultHttpContext();

        context.IsShareAccess().Should().BeFalse();

        context.SetShareAccess();

        context.IsShareAccess().Should().BeTrue();
    }

    [Fact]
    public void A_permission_trie_round_trips_and_answers_through_HasPermission()
    {
        var context = new DefaultHttpContext();
        var trie = new PermissionTrie();
        trie.Add(["api:entries:read"]);

        context.SetPermissionTrie(trie);

        context.GetPermissionTrie().Should().BeSameAs(trie);
        context.HasPermission("api:entries:read").Should().BeTrue();
        context.HasPermission("api:treatments:read").Should().BeFalse();
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// One request in a process stamps a row as last used per <see cref="LastUsedStamp.Interval"/>, however
/// many requests read the same stale stamp at once.
/// </summary>
[Trait("Category", "Unit")]
public class LastUsedClaimsTests
{
    private const int Requests = 32;

    private static readonly DateTime Start = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Start, TimeSpan.Zero));

    [Fact]
    public async Task ConcurrentClaimsOnOneRowHaveOneWinner()
    {
        var claims = new LastUsedClaims(_clock);
        var row = Guid.CreateVersion7();

        var won = await RaceAsync(() => claims.TryClaim(row, lastUsedAt: null));

        won.Count(w => w).Should().Be(1);
    }

    [Fact]
    public async Task ARowIsClaimedAgainOnceTheIntervalHasPassed()
    {
        var claims = new LastUsedClaims(_clock);
        var row = Guid.CreateVersion7();
        claims.TryClaim(row, null).Should().BeTrue();

        _clock.Advance(LastUsedStamp.Interval);
        claims.TryClaim(row, null).Should().BeFalse("the claim holds for the whole interval");

        _clock.Advance(TimeSpan.FromSeconds(1));
        var won = await RaceAsync(() => claims.TryClaim(row, null));

        won.Count(w => w).Should().Be(1);
    }

    [Fact]
    public void DifferentRowsAreClaimedIndependently()
    {
        var claims = new LastUsedClaims(_clock);

        claims.TryClaim(Guid.CreateVersion7(), null).Should().BeTrue();
        claims.TryClaim(Guid.CreateVersion7(), null).Should().BeTrue();
    }

    [Fact]
    public void ARowThatWasStampedWithinTheIntervalIsNotClaimed()
    {
        var claims = new LastUsedClaims(_clock);

        claims.TryClaim(Guid.CreateVersion7(), Start.AddMinutes(-4)).Should().BeFalse();
        claims.TryClaim(Guid.CreateVersion7(), Start.AddMinutes(-6)).Should().BeTrue();
    }

    [Fact]
    public async Task EveryCallerOfOneRowSharesTheClaim()
    {
        var claims = new LastUsedClaims(_clock);
        var row = Guid.CreateVersion7();

        var writes = new List<Guid>();
        var grants = new Mock<IOAuthGrantRepository>();
        grants
            .Setup(g => g.RecordUseAsync(_tenantId, row, It.IsAny<DateTime>(), It.IsAny<GrantPresentation?>(), It.IsAny<CancellationToken>()))
            .Callback(() => { lock (writes) writes.Add(row); })
            .Returns(Task.CompletedTask);
        grants
            .Setup(g => g.FindLiveDirectGrantAsync(_tenantId, It.IsAny<DirectGrantLookup>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedDirectGrant(Credential(row), Membership: null));

        var direct = new DirectGrantTokenHandler(grants.Object, _clock, claims, NullLogger<DirectGrantTokenHandler>.Instance);
        var apiKey = new ApiKeyHandler(grants.Object, _clock, claims, NullLogger<ApiKeyHandler>.Instance);

        await Task.WhenAll(
            Enumerable.Range(0, Requests).Select(i => Task.Run(() => i % 2 == 0
                ? direct.AuthenticateAsync(DirectGrantRequest())
                : apiKey.AuthenticateAsync(ApiKeyRequest()))));

        writes.Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentMemberRequestsStampTheMembershipOnce()
    {
        var membershipId = Guid.CreateVersion7();
        var writes = 0;
        var members = new Mock<ITenantMembershipRepository>();
        members
            .Setup(m => m.RecordUseAsync(_tenantId, membershipId, It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref writes))
            .Returns(Task.CompletedTask);
        var middleware = new MemberScopeMiddleware(
            _ => Task.CompletedTask, members.Object, new LastUsedClaims(_clock), NullLogger<MemberScopeMiddleware>.Instance);

        await Task.WhenAll(
            Enumerable.Range(0, Requests).Select(_ => Task.Run(() => middleware.InvokeAsync(MemberRequest(membershipId)))));

        await Task.Delay(250);
        writes.Should().Be(1);
    }

    private static async Task<bool[]> RaceAsync(Func<bool> claim)
    {
        using var gate = new ManualResetEventSlim();
        var racers = Enumerable.Range(0, Requests)
            .Select(_ => Task.Run(() =>
            {
                gate.Wait();
                return claim();
            }))
            .ToArray();

        gate.Set();
        return await Task.WhenAll(racers);
    }

    private DirectGrantCredential Credential(Guid id) =>
        new(id, _subjectId, [Scope.GlucoseRead], Label: null, LimitTo24Hours: false, LastUsedAt: null,
            IsMigrated: false, TokenHash: "hash", LegacySecretHash: null);

    private DefaultHttpContext DirectGrantRequest()
    {
        var context = TenantRequest();
        context.Request.Headers.Authorization = "Bearer noc_stale";
        return context;
    }

    private DefaultHttpContext ApiKeyRequest()
    {
        var context = TenantRequest();
        context.Request.Headers["api-secret"] = "noc_stale";
        return context;
    }

    private DefaultHttpContext TenantRequest()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantContext"] = new TenantContext(_tenantId, "default", "Default", true, IsDemo: false);
        return context;
    }

    private DefaultHttpContext MemberRequest(Guid membershipId)
    {
        var context = new DefaultHttpContext();
        context.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
            TenantId = _tenantId,
            Scopes = [],
        };
        context.Items["GrantedScopes"] = Scope.Normalize([]);
        context.Items["PermissionTrie"] = new PermissionTrie();
        new RequestMembership(
            _subjectId, _tenantId,
            new MembershipFacts(membershipId, new TenantMemberAccess(new HashSet<string> { Scope.GlucoseRead }, LimitTo24Hours: false), LastUsedAt: null))
            .StoreOn(context);
        return context;
    }
}

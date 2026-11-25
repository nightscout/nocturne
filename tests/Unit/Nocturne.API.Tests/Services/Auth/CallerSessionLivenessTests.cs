using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// A credential is minted only for a caller whose session is live: a session caller whose session
/// has ended is refused by both refusals, and every other credential is judged by its own record.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CallerSessionLivenessTests
{
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private const string SessionId = "session-1";

    private readonly Mock<IFirstPartyTokenRepository> _sessions = new(MockBehavior.Strict);

    private CallerSessionLiveness Liveness() => new(_sessions.Object, TimeProvider.System);

    private void SessionIs(string sessionId, bool live) =>
        _sessions
            .Setup(s => s.HasLiveSessionAsync(SubjectId, sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(live);

    private static AuthContext Caller(AuthType type, string? sessionId = SessionId) => new()
    {
        IsAuthenticated = true,
        AuthType = type,
        SubjectId = SubjectId,
        TenantId = TenantId,
        SessionId = sessionId,
    };

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task A_session_caller_whose_session_is_live_may_mint(AuthType type)
    {
        SessionIs(SessionId, live: true);

        (await Liveness().HasEndedAsync(Caller(type), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.LegacyJwt)]
    public async Task A_session_caller_whose_session_ended_may_not_mint(AuthType type)
    {
        SessionIs(SessionId, live: false);

        (await Liveness().HasEndedAsync(Caller(type), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_session_token_that_names_no_session_is_not_live_and_reads_nothing(string? sessionId)
    {
        (await Liveness().HasEndedAsync(Caller(AuthType.SessionCookie, sessionId), CancellationToken.None))
            .Should().BeTrue();

        _sessions.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.Guest)]
    [InlineData(AuthType.InstanceKey)]
    [InlineData(AuthType.PlatformAccess)]
    [InlineData(AuthType.None)]
    public async Task A_credential_that_is_not_a_session_is_judged_by_its_own_record_and_reads_nothing(AuthType type)
    {
        CallerSessionLiveness.IsSessionCaller(Caller(type)).Should().BeFalse();
        (await Liveness().HasEndedAsync(Caller(type, sessionId: null), CancellationToken.None)).Should().BeFalse();

        _sessions.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_session_caller_with_no_subject_is_not_judged()
    {
        var caller = Caller(AuthType.SessionCookie);
        caller.SubjectId = null;

        CallerSessionLiveness.IsSessionCaller(caller).Should().BeFalse();
        (await Liveness().HasEndedAsync(caller, CancellationToken.None)).Should().BeFalse();
        _sessions.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_answer_is_read_once_for_the_request_live_or_not(bool live)
    {
        SessionIs(SessionId, live);
        var liveness = Liveness();

        await liveness.HasEndedAsync(Caller(AuthType.SessionCookie), CancellationToken.None);
        (await liveness.HasEndedAsync(Caller(AuthType.SessionCookie), CancellationToken.None)).Should().Be(!live);

        _sessions.Verify(
            s => s.HasLiveSessionAsync(SubjectId, SessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Another_session_is_read_for_itself()
    {
        SessionIs(SessionId, live: false);
        SessionIs("session-2", live: true);
        var liveness = Liveness();

        (await liveness.HasEndedAsync(Caller(AuthType.SessionCookie), CancellationToken.None)).Should().BeTrue();
        (await liveness.HasEndedAsync(Caller(AuthType.SessionCookie, "session-2"), CancellationToken.None))
            .Should().BeFalse();
    }

    private HttpContext Request(AuthContext caller, bool subjectIsInactive = false, bool subjectRowActive = true)
    {
        var subjects = new Mock<ISubjectRepository>();
        subjects
            .Setup(s => s.GetSignInStateAsync(SubjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(subjectRowActive, IsPlatformAdmin: false));
        var services = new ServiceCollection()
            .AddSingleton(subjects.Object)
            .AddSingleton(_sessions.Object)
            .AddSingleton(TimeProvider.System)
            .AddScoped<CallerSessionLiveness>()
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
        http.Items["AuthContext"] = caller;
        new RequestMembership(SubjectId, TenantId, null) { SubjectIsInactive = subjectIsInactive }.StoreOn(http);
        return http;
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task The_tenant_refusal_refuses_an_inactive_subject_or_an_ended_session(
        bool subjectIsInactive, bool sessionLive, bool refused)
    {
        SessionIs(SessionId, sessionLive);
        var http = Request(Caller(AuthType.SessionCookie), subjectIsInactive: subjectIsInactive);

        (await http.IsCallerInactiveAsync(CancellationToken.None)).Should().Be(refused);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task The_subject_refusal_refuses_an_inactive_subject_or_an_ended_session(
        bool subjectRowActive, bool sessionLive, bool refused)
    {
        SessionIs(SessionId, sessionLive);
        var http = Request(Caller(AuthType.LegacyJwt), subjectRowActive: subjectRowActive);

        (await http.IsCallerSubjectInactiveAsync(CancellationToken.None)).Should().Be(refused);
    }

    [Fact]
    public async Task A_caller_that_is_not_a_session_needs_no_liveness_service()
    {
        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = Caller(AuthType.DirectGrant, sessionId: null);
        new RequestMembership(SubjectId, TenantId, null).StoreOn(http);

        (await http.IsCallerInactiveAsync(CancellationToken.None)).Should().BeFalse();
    }
}

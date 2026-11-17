using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The socket.io bridge joins a browser to the tenant-wide room on this answer, so it has to refuse
/// that room to exactly the credentials the SignalR hub refuses it to.
/// </summary>
[Trait("Category", "Unit")]
public class RealtimeAdmissionControllerTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static bool TenantRelayFor(AuthContext authContext) =>
        AdmissionFor(authContext).TenantRelay;

    private static RealtimeAdmission AdmissionFor(AuthContext authContext, params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(authContext);
        httpContext.SetTenantContext(
            new TenantContext(Tenant, "sleepy", "Sleepy", IsActive: true, IsDemo: false));
        httpContext.SetGrantedScopes(
            Scope.Normalize(scopes.Length == 0 ? [Scope.GlucoseRead] : scopes));

        var controller = new RealtimeAdmissionController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = controller.GetRealtimeAdmission().Result as OkObjectResult;
        result.Should().NotBeNull();
        return result!.Value.Should().BeOfType<RealtimeAdmission>().Subject;
    }

    [Fact]
    public void Admission_names_the_tenant_the_caller_was_admitted_to()
    {
        AdmissionFor(MemberSession()).TenantId.Should().Be(Tenant);
    }

    [Fact]
    public void Admission_is_refused_when_no_tenant_is_resolved()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(MemberSession());
        httpContext.SetGrantedScopes(Scope.Normalize([Scope.GlucoseRead]));
        var controller = new RealtimeAdmissionController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        controller.GetRealtimeAdmission().Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void A_member_session_may_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.CreateVersion7(),
        }).Should().BeTrue();
    }

    [Fact]
    public void An_api_secret_may_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext { IsAuthenticated = true, AuthType = AuthType.ApiKey })
            .Should().BeTrue();
    }

    [Fact]
    public void A_guest_session_holding_glucose_may_not_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest })
            .Should().BeFalse();
    }

    [Fact]
    public void An_anonymous_public_share_may_not_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.None,
            SubjectId = Guid.CreateVersion7(),
            TenantId = Tenant,
        }).Should().BeFalse();
    }

    [Fact]
    public void A_member_session_admission_names_the_subject_to_room_on()
    {
        // The bridge carries this id into a per-subject room; without it the member's socket
        // receives none of its own notifications.
        var subject = Guid.CreateVersion7();

        AdmissionFor(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = subject,
        }).SubjectId.Should().Be(subject);
    }

    [Fact]
    public void A_guest_session_admission_names_no_subject()
    {
        AdmissionFor(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            SubjectId = Guid.CreateVersion7(),
        }).SubjectId.Should().BeNull();
    }

    [Fact]
    public void An_anonymous_share_admission_names_no_subject()
    {
        AdmissionFor(new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.None,
            SubjectId = Guid.CreateVersion7(),
            TenantId = Tenant,
        }).SubjectId.Should().BeNull();
    }

    private static AuthContext MemberSession(bool limitTo24Hours = false) => new()
    {
        IsAuthenticated = true,
        AuthType = AuthType.SessionCookie,
        SubjectId = Guid.CreateVersion7(),
        LimitTo24Hours = limitTo24Hours,
    };

    [Fact]
    public void Admission_names_only_the_categories_the_caller_holds_a_read_scope_for()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead, Scope.TreatmentsRead).Categories
            .Should().BeEquivalentTo(["entries", "glucose", "treatments", "care"]);
    }

    [Fact]
    public void A_glucose_only_caller_is_admitted_to_no_treatment_or_device_category()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead).Categories
            .Should().BeEquivalentTo(["entries", "glucose"]);
    }

    [Fact]
    public void A_history_clamped_caller_is_admitted_to_no_category()
    {
        var admission = AdmissionFor(MemberSession(limitTo24Hours: true), Scope.FullAccess);

        admission.Categories.Should().BeEmpty();
        admission.HistoryClamped.Should().BeTrue();
    }

    [Theory]
    [InlineData(Scope.GlucoseRead, new[] { "entries", "glucose" })]
    [InlineData(Scope.TreatmentsRead, new[] { "treatments", "care" })]
    public void A_guest_session_is_admitted_to_the_categories_it_holds_a_read_scope_for(string scope, string[] expected)
    {
        AdmissionFor(
            new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest },
            scope).Categories.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void A_history_clamped_guest_session_is_admitted_to_no_category()
    {
        AdmissionFor(
            new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest, LimitTo24Hours = true },
            Scope.GlucoseRead, Scope.TreatmentsRead).Categories.Should().BeEmpty();
    }

    [Fact]
    public void An_anonymous_share_is_admitted_to_no_category()
    {
        AdmissionFor(
            new AuthContext
            {
                IsAuthenticated = false,
                AuthType = AuthType.None,
                SubjectId = Guid.CreateVersion7(),
                TenantId = Tenant,
            },
            Scope.GlucoseRead, Scope.TreatmentsRead).Categories.Should().BeEmpty();
    }

    [Fact]
    public void A_caller_holding_alerts_readwrite_may_silence_alarms()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead, Scope.AlertsReadWrite).MayAck.Should().BeTrue();
    }

    [Fact]
    public void A_caller_holding_alerts_acknowledge_may_silence_alarms()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead, Scope.AlertsAcknowledge).MayAck.Should().BeTrue();
    }

    [Fact]
    public void A_caller_holding_only_device_notify_may_not_silence_alarms()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead, Scope.DeviceNotify).MayAck.Should().BeFalse();
    }

    [Fact]
    public void A_caller_holding_only_glucose_read_may_not_silence_alarms()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead).MayAck.Should().BeFalse();
    }

    [Fact]
    public void A_read_only_follower_may_not_silence_alarms()
    {
        AdmissionFor(MemberSession(), Scope.GlucoseRead, Scope.AlertsRead).MayAck.Should().BeFalse();
    }

    [Fact]
    public void A_guest_session_may_not_silence_alarms_whatever_its_scopes()
    {
        AdmissionFor(
            new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest },
            Scope.GlucoseRead, Scope.AlertsReadWrite).MayAck.Should().BeFalse();
    }

    [Fact]
    public void Admission_requires_the_glucose_read_a_live_glucose_feed_needs()
    {
        var method = typeof(RealtimeAdmissionController)
            .GetMethod(nameof(RealtimeAdmissionController.GetRealtimeAdmission))!;

        method.GetCustomAttributes(inherit: true).OfType<RequireScopeAttribute>()
            .Should().ContainSingle()
            .Which.Scopes.Should().Equal(Scope.GlucoseRead);
    }

    [Fact]
    public void Admission_does_not_401_an_anonymous_share()
    {
        typeof(RealtimeAdmissionController).GetCustomAttributes(inherit: true)
            .OfType<IAuthorizeData>()
            .Should().BeEmpty();
    }
}

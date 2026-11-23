using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.RateLimiting;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// Team creation is limited per person: two people behind one address each get the whole
/// allowance, and one person gets no more by changing address. Every case runs inside one window.
/// </summary>
public class TeamCreationLimiterTests : IDisposable
{
    private readonly TeamCreationLimiter _limiter = new(new ConfigurationBuilder().Build());

    public void Dispose()
    {
        _limiter.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheCreateAction_CarriesTheLimit() =>
        typeof(TeamsController).GetMethod(nameof(TeamsController.CreateTeam))!
            .GetCustomAttribute<LimitPerPersonAttribute<TeamCreationLimiter>>()
            .Should().NotBeNull();

    [Fact]
    public void TheRenameAction_CarriesItsOwnLimit() =>
        typeof(TeamsController).GetMethod(nameof(TeamsController.RenameTeam))!
            .GetCustomAttribute<LimitPerPersonAttribute<TeamRenameLimiter>>()
            .Should().NotBeNull();

    [Fact]
    public void Two_people_behind_one_address_each_get_the_whole_allowance()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        Spend(first, "10.0.0.1").Should().Be(_limiter.PermitLimit);
        Spend(second, "10.0.0.1").Should().Be(_limiter.PermitLimit);
    }

    [Fact]
    public void One_person_gets_no_more_by_changing_address()
    {
        var person = Guid.CreateVersion7();

        Spend(person, "10.0.0.1").Should().Be(_limiter.PermitLimit);
        Spend(person, "10.0.0.2").Should().Be(0);
    }

    [Fact]
    public void A_request_with_no_subject_is_limited_by_its_address()
    {
        Spend(null, "10.0.0.3").Should().Be(_limiter.PermitLimit);
        Spend(null, "10.0.0.4").Should().Be(_limiter.PermitLimit);
    }

    /// <summary>How many creations the caller gets before the first refusal, up to one past the limit.</summary>
    private int Spend(Guid? subjectId, string address)
    {
        var granted = 0;
        for (var i = 0; i <= _limiter.PermitLimit; i++)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
            context.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = subjectId is not null, AuthType = AuthType.SessionCookie, SubjectId = subjectId,
            };
            using var lease = _limiter.Acquire(context);
            if (!lease.IsAcquired)
                break;
            granted++;
        }

        return granted;
    }
}

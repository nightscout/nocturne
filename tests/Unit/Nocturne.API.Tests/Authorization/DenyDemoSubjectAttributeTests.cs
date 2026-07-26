using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// A demo session is handed to any anonymous caller, so the subject behind it is
/// authenticated but stands for no one. The tenantless, subject-scoped endpoints — creating
/// a tenant, accepting an invite, requesting membership — have no tenant to check
/// permissions against, so this filter is the only thing keeping an anonymous visitor from
/// acting as a platform user.
/// </summary>
public class DenyDemoSubjectAttributeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    public DenyDemoSubjectAttributeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var seed = new NocturneDbContext(_dbOptions);
        seed.Database.EnsureCreated();
    }

    [Fact]
    public async Task OnAuthorizationAsync_ForbidsADemoSubject()
    {
        var subjectId = SeedSubject(isDemoSubject: true);
        var context = BuildContext(subjectId);

        await new DenyDemoSubjectAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task OnAuthorizationAsync_AllowsAnOrdinarySubject()
    {
        var subjectId = SeedSubject(isDemoSubject: false);
        var context = BuildContext(subjectId);

        await new DenyDemoSubjectAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task OnAuthorizationAsync_DefersWhenUnauthenticated()
    {
        // No AuthContext: the endpoint's own [Authorize] decides, not this filter.
        var context = BuildContext(subjectId: null);

        await new DenyDemoSubjectAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task OnAuthorizationAsync_ForbidsASubjectThatNoLongerExists()
    {
        // Access tokens are self-contained JWTs with no revocation check, and every demo
        // reset deletes the demo subject — a missing row must not read as "not a demo
        // subject" and be waved through.
        var context = BuildContext(Guid.CreateVersion7());

        await new DenyDemoSubjectAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<ForbidResult>();
    }

    private Guid SeedSubject(bool isDemoSubject)
    {
        using var db = new NocturneDbContext(_dbOptions);
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = isDemoSubject ? "Demo Visitor" : "Real Person",
            IsActive = true,
            IsDemoSubject = isDemoSubject,
        };
        db.Subjects.Add(subject);
        db.SaveChanges();
        return subject.Id;
    }

    private AuthorizationFilterContext BuildContext(Guid? subjectId)
    {
        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));

        var services = new ServiceCollection();
        services.AddSingleton(dbFactory.Object);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };

        if (subjectId is not null)
        {
            httpContext.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true,
                SubjectId = subjectId,
            };
        }

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            []);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}

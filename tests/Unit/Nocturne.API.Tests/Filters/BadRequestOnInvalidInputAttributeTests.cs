using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Analytics;

namespace Nocturne.API.Tests.Filters;

[Trait("Category", "Unit")]
public class BadRequestOnInvalidInputAttributeTests
{
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddControllers()
        .Services.BuildServiceProvider();

    private static ExceptionContext Run(Exception exception)
    {
        var httpContext = new DefaultHttpContext { RequestServices = Services };
        var context = new ExceptionContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>()
        )
        {
            Exception = exception,
        };

        new BadRequestOnInvalidInputAttribute().OnException(context);
        return context;
    }

    [Theory]
    [MemberData(nameof(InputFaults))]
    public void AnInputFaultBecomesA400ProblemDetailsCarryingTheMessageInDetail(Exception exception)
    {
        var context = Run(exception);

        context.ExceptionHandled.Should().BeTrue();
        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(400);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(400);
        problem.Title.Should().Be("Bad Request");
        problem.Detail.Should().Be(exception.Message);
    }

    /// <summary>
    /// <see cref="ObjectDisposedException"/> derives from <see cref="InvalidOperationException"/>,
    /// so a context used after disposal is answered as a client error. That is what the one action
    /// already catching the two types by name did, and it is kept rather than special-cased.
    /// </summary>
    public static TheoryData<Exception> InputFaults =>
        [
            new ArgumentException("bad values"),
            new ArgumentNullException("entries"),
            new InvalidOperationException("no profile loaded"),
            new ObjectDisposedException("NocturneDbContext"),
        ];

    /// <summary>
    /// A cancelled request and a server fault both have to reach the global handler; answering
    /// either with a 400 reports a server-side failure as the caller's fault.
    /// </summary>
    [Theory]
    [MemberData(nameof(PropagatedExceptions))]
    public void EveryOtherExceptionIsLeftUnhandled(Exception exception)
    {
        var context = Run(exception);

        context.ExceptionHandled.Should().BeFalse();
        context.Result.Should().BeNull();
    }

    public static TheoryData<Exception> PropagatedExceptions =>
        [
            new OperationCanceledException(),
            new TaskCanceledException(),
            new TimeoutException(),
            new DbUpdateException("the update failed"),
            new Exception("connection reset"),
        ];

    /// <summary>
    /// The filter changes nothing unless it is applied, so the controller the statistics stack
    /// answers from has to carry it.
    /// </summary>
    [Fact]
    public void StatisticsControllerCarriesTheAttribute()
    {
        typeof(StatisticsController)
            .GetCustomAttributes(typeof(BadRequestOnInvalidInputAttribute), inherit: false)
            .Should()
            .HaveCount(1);
    }
}

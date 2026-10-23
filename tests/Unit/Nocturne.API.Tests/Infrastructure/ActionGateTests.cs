using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Infrastructure;

[Trait("Category", "Unit")]
public class ActionGateTests
{
    private static readonly IReadOnlySet<string> NoScopes = new HashSet<string>();

    [Fact]
    public void RunsTheAuthorizationFilterBeforeTheActionFilter()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        var result = ActionGate.Evaluate(
            typeof(GatedController), nameof(GatedController.Both), NoScopes, new AuthContext());

        result.Should().BeNull();
        trail.Should().Equal("authorization", "action");
    }

    [Fact]
    public void AnAuthorizationRefusalStopsBeforeTheActionFilter()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        var result = ActionGate.Evaluate(
            typeof(GatedController), nameof(GatedController.RefusedByAuthorization), NoScopes, new AuthContext());

        result.Should().BeOfType<ForbidResult>();
        trail.Should().Equal("authorization");
    }

    [Fact]
    public void AnActionFilterRefusalIsReturned()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        var result = ActionGate.Evaluate(
            typeof(GatedController), nameof(GatedController.RefusedByAction), NoScopes, new AuthContext());

        result.Should().BeOfType<UnauthorizedResult>();
        trail.Should().Equal("authorization", "action");
    }

    [Fact]
    public void ARefusingActionFilterStopsTheOnesAfterIt()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        var result = ActionGate.Evaluate(
            typeof(GatedController), nameof(GatedController.TwoActionFilters), NoScopes, new AuthContext());

        result.Should().BeOfType<UnauthorizedResult>();
        trail.Should().Equal("action");
    }

    [Fact]
    public void RunsTheControllersFiltersBeforeTheMethodsAtTheSameOrder()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        ActionGate.Evaluate(
            typeof(ClassGatedController), nameof(ClassGatedController.Handler), NoScopes, new AuthContext());

        trail.Should().Equal("class", "method");
    }

    [Fact]
    public void ALowerOrderRunsFirstRegardlessOfScope()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        ActionGate.Evaluate(
            typeof(OrderedController), nameof(OrderedController.Handler), NoScopes, new AuthContext());

        trail.Should().Equal("method", "class");
    }

    [Fact]
    public void PassesTheCallersScopesAndAuthContextToTheFilters()
    {
        var granted = new HashSet<string> { Scope.AlertsRead };
        var auth = new AuthContext { IsAuthenticated = true };

        ActionGate.Evaluate(typeof(InspectingController), nameof(InspectingController.Handler), granted, auth);
        InspectingAttribute.Seen.GrantedScopes.Should().BeSameAs(granted);
        InspectingAttribute.Seen.AuthContext.Should().BeSameAs(auth);

        ActionGate.Evaluate(typeof(InspectingController), nameof(InspectingController.Handler), granted);
        InspectingAttribute.Seen.AuthContext.Should().BeNull();
    }

    [Fact]
    public void FiltersThatAlsoImplementTheAsynchronousInterfaceAreSkipped()
    {
        var trail = new List<string>();
        using var _ = Recording.Begin(trail);

        var result = ActionGate.Evaluate(
            typeof(AsyncCapableController), nameof(AsyncCapableController.Handler), NoScopes, new AuthContext());

        result.Should().BeNull();
        trail.Should().BeEmpty();
    }

    [Fact]
    public void AMissingMethodNamesTheController()
    {
        var act = () => ActionGate.Evaluate(typeof(GatedController), "Gone", NoScopes);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GatedController*Gone*");
    }

    private sealed class Recording : IDisposable
    {
        [ThreadStatic] private static List<string>? current;

        public static List<string> Trail => current ?? throw new InvalidOperationException("No trail started");

        public static Recording Begin(List<string> trail)
        {
            current = trail;
            return new Recording();
        }

        public void Dispose() => current = null;
    }

    private sealed class RecordingAuthorizationAttribute(string name = "authorization", bool refuse = false)
        : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            Recording.Trail.Add(name);
            if (refuse)
                context.Result = new ForbidResult();
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    private sealed class RecordingActionAttribute(string name = "action", bool refuse = false, int order = 0)
        : Attribute, IActionFilter, IOrderedFilter
    {
        public int Order => order;

        public void OnActionExecuting(ActionExecutingContext context)
        {
            Recording.Trail.Add(name);
            if (refuse)
                context.Result = new UnauthorizedResult();
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }

    private sealed class OrderedRecordingAttribute(string name, int order) : Attribute, IAuthorizationFilter, IOrderedFilter
    {
        public int Order => order;

        public void OnAuthorization(AuthorizationFilterContext context) => Recording.Trail.Add(name);
    }

    private sealed class AsyncCapableAuthorizationAttribute : Attribute, IAuthorizationFilter, IAsyncAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            Recording.Trail.Add("sync authorization");
            context.Result = new ForbidResult();
        }

        public Task OnAuthorizationAsync(AuthorizationFilterContext context) => Task.CompletedTask;
    }

    private sealed class AsyncCapableActionAttribute : Attribute, IActionFilter, IAsyncActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            Recording.Trail.Add("sync action");
            context.Result = new UnauthorizedResult();
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }

        public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next) =>
            next();
    }

    private sealed class InspectingAttribute : Attribute, IAuthorizationFilter
    {
        public static (IReadOnlySet<string>? GrantedScopes, AuthContext? AuthContext) Seen { get; private set; }

        public void OnAuthorization(AuthorizationFilterContext context) =>
            Seen = (
                context.HttpContext.Items["GrantedScopes"] as IReadOnlySet<string>,
                context.HttpContext.Items["AuthContext"] as AuthContext);
    }

    private sealed class GatedController
    {
        [RecordingAction(order: -1), RecordingAuthorization]
        public void Both() { }

        [RecordingAction(order: -1), RecordingAuthorization(refuse: true)]
        public void RefusedByAuthorization() { }

        [RecordingAction(refuse: true, order: -1), RecordingAuthorization]
        public void RefusedByAction() { }

        [RecordingAction(refuse: true, order: 0), RecordingAction("never reached", order: 1)]
        public void TwoActionFilters() { }
    }

    [OrderedRecording("class", 0)]
    private sealed class ClassGatedController
    {
        [OrderedRecording("method", 0)]
        public void Handler() { }
    }

    [OrderedRecording("class", 1)]
    private sealed class OrderedController
    {
        [OrderedRecording("method", 0)]
        public void Handler() { }
    }

    private sealed class AsyncCapableController
    {
        [AsyncCapableAuthorization, AsyncCapableAction]
        public void Handler() { }
    }

    private sealed class InspectingController
    {
        [Inspecting]
        public void Handler() { }
    }
}

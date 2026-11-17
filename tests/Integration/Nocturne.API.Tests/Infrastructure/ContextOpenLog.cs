using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Every context the API's <see cref="INocturneDbContextFactory"/> hands out while a tagged request is
/// in the pipeline, on the request thread or on work the request started and did not await, with its
/// kind, its caller and whether the controller action had started. Opens after the request has left
/// the pipeline are not logged, though work it started may still be running.
/// </summary>
/// <remarks>
/// A context opens on whatever thread asks for it, and work the request did not await runs beside
/// the request on its execution context, so an <see cref="IHttpContextAccessor"/> there returns the
/// live request. Reading any request feature there can land while the request thread is inserting a
/// feature of its own: the read sees the bumped revision and clears the request's feature cache, and
/// the request thread reads back null (#3064: <c>HasFormContentType</c> threw in model binding). So
/// the tag is read once, on the request thread at the head of the pipeline, and an open reads only
/// the <see cref="RequestTag"/> that flows with the execution context.
/// </remarks>
public sealed class ContextOpenLog
{
    private const string RequestIdHeader = "X-Context-Log-Id";

    private readonly ConcurrentDictionary<string, ConcurrentQueue<ContextOpen>> _opens = new();
    private readonly AsyncLocal<RequestTag?> _current = new();

    public sealed record ContextOpen(string Kind, string Caller, bool BeforeAction);

    /// <summary>Tags <paramref name="request"/> so its opens can be read back afterwards.</summary>
    public static string Tag(HttpRequestMessage request)
    {
        var id = Guid.NewGuid().ToString("N");
        request.Headers.Add(RequestIdHeader, id);
        return id;
    }

    public IReadOnlyList<ContextOpen> OpensOf(string requestId) =>
        _opens.TryGetValue(requestId, out var opens) ? opens.ToList() : [];

    /// <summary>
    /// Wraps the registered factory so every context it creates is logged against the request it
    /// was created for, and marks when each request's controller action starts.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ContextOpenLog>();
        services.AddTransient<IStartupFilter, TagReader>();
        services.Configure<MvcOptions>(options => options.Filters.Add(new ActionStartedFilter()));

        var inner = services.Last(d => d.ServiceType == typeof(INocturneDbContextFactory));
        services.Remove(inner);
        services.Add(ServiceDescriptor.Describe(
            typeof(INocturneDbContextFactory),
            sp => new LoggingFactory(
                (INocturneDbContextFactory)inner.ImplementationFactory!(sp),
                sp.GetRequiredService<ContextOpenLog>()),
            inner.Lifetime));
    }

    private void Record(string kind)
    {
        if (_current.Value is not { Ended: false } tag)
            return;

        _opens.GetOrAdd(tag.Id, _ => new())
            .Enqueue(new ContextOpen(kind, CallerOf(new StackTrace()), !tag.ActionStarted));
    }

    private static string CallerOf(StackTrace trace)
    {
        foreach (var frame in trace.GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            var outer = type?.DeclaringType ?? type;
            if (outer?.Namespace is { } ns
                && ns.StartsWith("Nocturne.", StringComparison.Ordinal)
                && outer != typeof(ContextOpenLog)
                && outer != typeof(LoggingFactory)
                && !ns.StartsWith("Nocturne.API.Tests", StringComparison.Ordinal))
            {
                return type!.DeclaringType is not null ? $"{outer.Name}.{type.Name}" : $"{outer.Name}.{frame.GetMethod()!.Name}";
            }
        }

        return "?";
    }

    private sealed class RequestTag(string id)
    {
        private volatile bool _actionStarted;
        private volatile bool _ended;

        public string Id { get; } = id;

        public bool ActionStarted => _actionStarted;

        public bool Ended => _ended;

        public void MarkActionStarted() => _actionStarted = true;

        public void End() => _ended = true;
    }

    private sealed class TagReader(ContextOpenLog log) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers[RequestIdHeader].FirstOrDefault() is not { } id)
                {
                    await nextMiddleware(context);
                    return;
                }

                var tag = new RequestTag(id);
                log._current.Value = tag;
                try
                {
                    await nextMiddleware(context);
                }
                finally
                {
                    tag.End();
                }
            });
            next(app);
        };
    }

    private sealed class ActionStartedFilter : IAsyncActionFilter, IOrderedFilter
    {
        public int Order => int.MinValue;

        public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            context.HttpContext.RequestServices.GetRequiredService<ContextOpenLog>()._current.Value?.MarkActionStarted();
            return next();
        }
    }

    private sealed class LoggingFactory(INocturneDbContextFactory inner, ContextOpenLog log) : INocturneDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
        {
            log.Record("scope");
            return inner.CreateAsync(ct);
        }

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default)
        {
            log.Record("tenant");
            return inner.CreateForTenantAsync(tenantId, ct);
        }

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
        {
            log.Record("subject");
            return inner.CreateForSubjectAsync(subjectId, ct);
        }

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default)
        {
            log.Record("platform");
            return inner.CreatePlatformAsync(ct);
        }

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default)
        {
            log.Record("system-pinned");
            return inner.CreateSystemPinnedAsync(tenantId, ct);
        }
    }
}

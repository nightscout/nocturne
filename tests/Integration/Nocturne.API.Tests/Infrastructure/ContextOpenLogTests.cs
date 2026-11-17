using System.Collections;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

public class ContextOpenLogTests
{
    /// <summary>
    /// Replays the interleaving in <see cref="ContextOpenLog"/>'s remarks, with the open on
    /// <see cref="Task.Run(Func{Task})"/> as <c>MemberScopeMiddleware</c>'s last-used stamp makes it.
    /// </summary>
    [Fact]
    public async Task A_context_opened_by_work_the_request_did_not_await_is_logged_without_touching_the_request()
    {
        await using var provider = Provider();
        var factory = provider.GetRequiredService<INocturneDbContextFactory>();

        var features = new FeaturesThatYieldOnFormFeature(() =>
            Task.Run(() => factory.CreateForTenantAsync(Guid.CreateVersion7()).AsTask()).GetAwaiter().GetResult());
        var (context, requestId) = TaggedRequest(provider, features);

        await Pipeline(provider, ctx =>
        {
            _ = ctx.Request.HasFormContentType;
            return Task.CompletedTask;
        })(context);

        provider.GetRequiredService<ContextOpenLog>().OpensOf(requestId)
            .Should().ContainSingle().Which.Kind.Should().Be("tenant");
    }

    [Fact]
    public async Task A_context_opened_after_the_request_left_the_pipeline_is_not_logged_against_it()
    {
        await using var provider = Provider();
        var factory = provider.GetRequiredService<INocturneDbContextFactory>();
        var (context, requestId) = TaggedRequest(provider, new FeatureCollection());
        var released = new TaskCompletionSource();
        Task? detached = null;

        await Pipeline(provider, _ =>
        {
            detached = Task.Run(async () =>
            {
                await released.Task;
                await factory.CreateForTenantAsync(Guid.CreateVersion7());
            });
            return Task.CompletedTask;
        })(context);
        released.SetResult();
        await detached!;

        provider.GetRequiredService<ContextOpenLog>().OpensOf(requestId).Should().BeEmpty();
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddTransient(_ => Mock.Of<INocturneDbContextFactory>());
        ContextOpenLog.Register(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A request on <paramref name="features"/> carrying a <see cref="ContextOpenLog"/> tag, made the
    /// accessor's current request as hosting does before the pipeline runs.
    /// </summary>
    private static (HttpContext Context, string RequestId) TaggedRequest(
        IServiceProvider provider, IFeatureCollection features)
    {
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Method = "POST" });
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        var context = new DefaultHttpContext(features) { RequestServices = provider };

        using var message = new HttpRequestMessage();
        var requestId = ContextOpenLog.Tag(message);
        foreach (var (name, values) in message.Headers)
            context.Request.Headers[name] = values.ToArray();

        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return (context, requestId);
    }

    private static RequestDelegate Pipeline(IServiceProvider provider, RequestDelegate endpoint)
    {
        Action<IApplicationBuilder> configure = app => app.Run(endpoint);
        foreach (var filter in provider.GetServices<IStartupFilter>().Reverse())
            configure = filter.Configure(configure);

        var builder = new ApplicationBuilder(provider);
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Runs <paramref name="onFormFeature"/> to completion between the form feature's insertion,
    /// which bumps the collection's revision, and the request's read of it back from its cache.
    /// </summary>
    private sealed class FeaturesThatYieldOnFormFeature(Action onFormFeature) : IFeatureCollection
    {
        private readonly FeatureCollection _inner = new();
        private bool _yielded;

        public bool IsReadOnly => false;

        public int Revision => _inner.Revision;

        public object? this[Type key]
        {
            get => _inner[key];
            set
            {
                _inner[key] = value;
                YieldIfForm(key);
            }
        }

        public TFeature? Get<TFeature>() => _inner.Get<TFeature>();

        public void Set<TFeature>(TFeature? instance)
        {
            _inner.Set(instance);
            YieldIfForm(typeof(TFeature));
        }

        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void YieldIfForm(Type key)
        {
            if (key != typeof(IFormFeature) || _yielded)
                return;

            _yielded = true;
            onFormFeature();
        }
    }
}

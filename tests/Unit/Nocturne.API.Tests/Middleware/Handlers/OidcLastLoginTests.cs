using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Auth;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// <see cref="OidcTokenHandler.RecordLastLoginAsync"/> is started fire-and-forget and outlives the
/// handler's own scope, so it must hold the scope its write resolves from until the write finishes.
/// </summary>
[Trait("Category", "Unit")]
public class OidcLastLoginTests
{
    [Fact]
    public async Task TheWriteRunsOnAScopeThatOutlivesTheCaller()
    {
        var subjectId = Guid.CreateVersion7();
        var gate = new TaskCompletionSource();
        var subjects = new GatedSubjects(gate.Task);

        await using var provider = new ServiceCollection()
            .AddScoped<ISubjectService>(sp => sp.GetRequiredService<GatedSubjects>().Object)
            .AddScoped(_ => subjects)
            .BuildServiceProvider();

        Task write;
        using (var callerScope = provider.CreateScope())
        {
            var scopeFactory = callerScope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
            write = OidcTokenHandler.RecordLastLoginAsync(scopeFactory, NullLogger.Instance, subjectId);
        }

        gate.SetResult();
        await write;

        subjects.Written.Should().Equal(subjectId);
        subjects.DisposedBeforeWriteFinished.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedWriteDoesNotReachTheCaller()
    {
        var subjects = new Mock<ISubjectService>();
        subjects.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        await using var provider = new ServiceCollection()
            .AddScoped(_ => subjects.Object)
            .BuildServiceProvider();

        var act = () => OidcTokenHandler.RecordLastLoginAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, Guid.CreateVersion7());

        await act.Should().NotThrowAsync();
    }

    private sealed class GatedSubjects : IDisposable
    {
        private bool _disposed;

        public GatedSubjects(Task gate)
        {
            var mock = new Mock<ISubjectService>();
            mock.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()))
                .Returns<Guid>(async id =>
                {
                    await gate;
                    DisposedBeforeWriteFinished = _disposed;
                    Written.Add(id);
                });
            Object = mock.Object;
        }

        public ISubjectService Object { get; }
        public List<Guid> Written { get; } = [];
        public bool DisposedBeforeWriteFinished { get; private set; }

        public void Dispose() => _disposed = true;
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Notifications;

public sealed class NotificationResolutionServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db =
        TestDbContextFactory.CreateSqliteWithTenant(Guid.NewGuid(), "resolution");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ANotificationIsArchivedAsExpiredFromTheInstantItExpires()
    {
        var beforeExpiry = Notification(Now.UtcDateTime.AddTicks(1));
        var atExpiry = Notification(Now.UtcDateTime);
        var afterExpiry = Notification(Now.UtcDateTime.AddTicks(-1));

        var archived = await ArchivedInOnePassAsync([beforeExpiry, afterExpiry, atExpiry], expected: 2);

        Assert.Equal(
            new[] { afterExpiry.Id, atExpiry.Id }.Order(),
            archived.Select(a => a.Id).Order());
        Assert.All(archived, a => Assert.Equal(NotificationArchiveReason.Expired, a.Reason));
    }

    private async Task<List<(Guid Id, NotificationArchiveReason Reason)>> ArchivedInOnePassAsync(
        List<InAppNotificationEntity> pending,
        int expected)
    {
        var archived = new ConcurrentQueue<(Guid, NotificationArchiveReason)>();
        var allArchived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.GetPendingResolutionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        repository
            .Setup(r => r.ArchiveAsync(It.IsAny<Guid>(), It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, NotificationArchiveReason reason, CancellationToken _) =>
            {
                archived.Enqueue((id, reason));
                if (archived.Count >= expected)
                    allArchived.TrySetResult();
            })
            .ReturnsAsync((InAppNotificationEntity?)null);

        var clock = new FakeTimeProvider(Now);
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, RecordingTenantAccessor>();
        services.AddScoped(_ => repository.Object);
        services.AddScoped(_ => Mock.Of<ISignalRBroadcastService>());
        services.AddSingleton(new ActiveTenantSnapshot(
            new TenantRepository(_db.ContextFactory.AsNocturneFactory()), clock, NullLogger<ActiveTenantSnapshot>.Instance));
        await using var provider = services.BuildServiceProvider();
        using var service = new NotificationResolutionService(
            provider,
            provider.GetRequiredService<ActiveTenantSnapshot>(),
            clock,
            NullLogger<NotificationResolutionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await allArchived.Task.WaitAsync(Patience);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        return [.. archived];
    }

    private static InAppNotificationEntity Notification(DateTime expiresAt) => new()
    {
        Id = Guid.CreateVersion7(),
        ResolutionConditionsJson = JsonSerializer.Serialize(
            new ResolutionConditions { ExpiresAt = expiresAt },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
    };

    private sealed class RecordingTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;

        public void SetTenant(TenantContext context) => Context = context;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A notification store for the team announcers: each site's scope reads and writes the same list,
/// keyed by the tenant its accessor resolved, with the active-notice rules the real service keeps. Safe
/// for announcers that run at once.
/// </summary>
internal sealed class RecordingNotices
{
    internal sealed record Notice(Guid? Tenant, string User, string Type, string Source, string Title, Dictionary<string, object>? Metadata)
    {
        public NotificationArchiveReason? Archived { get; set; }
    }

    private readonly List<Notice> _notices = [];
    private readonly Lock _sync = new();

    public Exception? Failure { get; set; }

    /// <summary>Awaited before each archive or create, so a test can hold an announcer part way through its filing.</summary>
    public Func<Task>? BeforeWrite { get; set; }

    public IReadOnlyList<Notice> All
    {
        get
        {
            lock (_sync)
                return _notices.ToList();
        }
    }

    public IEnumerable<Notice> Live => All.Where(n => n.Archived is null);

    private Task WriteAsync() => BeforeWrite?.Invoke() ?? Task.CompletedTask;

    public IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, Accessor>();
        services.AddScoped(provider =>
        {
            Guid? Tenant() => provider.GetRequiredService<ITenantAccessor>().Context?.TenantId;
            var notifications = new Mock<IInAppNotificationService>();
            var read = notifications.Setup(n => n.GetActiveNotificationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()));
            if (Failure is not null)
            {
                read.ThrowsAsync(Failure);
                notifications.Setup(n => n.ArchiveBySourceAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationArchiveReason>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(Failure);
                return notifications.Object;
            }

            read.ReturnsAsync((string user, CancellationToken _) => Live
                .Where(n => n.Tenant == Tenant() && n.User == user)
                .Select(n => new InAppNotificationDto { Type = n.Type, SourceId = n.Source, Title = n.Title, Metadata = n.Metadata })
                .ToList());
            notifications.Setup(n => n.ArchiveBySourceAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationArchiveReason>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (string user, string type, string source, NotificationArchiveReason reason, CancellationToken _) =>
                {
                    await WriteAsync();
                    lock (_sync)
                    {
                        var live = _notices.FirstOrDefault(n =>
                            n.Archived is null && n.Tenant == Tenant() && n.User == user && n.Type == type && n.Source == source);
                        if (live is null)
                            return false;
                        live.Archived = reason;
                        return true;
                    }
                });
            notifications.Setup(n => n.CreateNotificationUnlessActiveAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<NotificationCategory?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
                .Returns(async (string user, string type, string title, string source, NotificationCategory? _, string? _,
                    string? _, Dictionary<string, object>? metadata, CancellationToken _) =>
                {
                    await WriteAsync();
                    lock (_sync)
                    {
                        if (_notices.Any(n =>
                                n.Archived is null && n.Tenant == Tenant() && n.User == user && n.Type == type && n.Source == source))
                            return false;
                        _notices.Add(new Notice(Tenant(), user, type, source, title, metadata));
                        return true;
                    }
                });
            return notifications.Object;
        });
        return services.BuildServiceProvider();
    }

    private sealed class Accessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public bool IsResolved => Context is not null;

        public TenantContext? Context { get; private set; }

        public void SetTenant(TenantContext context) => Context = context;
    }
}

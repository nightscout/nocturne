using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>An <see cref="IHeldRuleNotice"/> for a tenant with no held rule: none due, nothing sent.</summary>
internal static class NoHeldRules
{
    public static IHeldRuleNotice Notice()
    {
        var notice = new Mock<IHeldRuleNotice>();
        notice.Setup(n => n.DueAsync(It.IsAny<IReadOnlyList<AlertRuleSnapshot>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return notice.Object;
    }
}

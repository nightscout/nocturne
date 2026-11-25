using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Reports;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Npgsql;

namespace Nocturne.API.Tests.Reports;

[Trait("Category", "Unit")]
public class ReportReadAuditTests
{
    public static TheoryData<Exception> UnreadableSettings => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
        new TimeoutException(),
    };

    [Theory]
    [MemberData(nameof(UnreadableSettings))]
    public async Task WriteAsync_WhenTheAuditSettingCannotBeRead_SkipsTheRowAndTheRun(Exception failure)
    {
        var cache = new Mock<ITenantAuditConfigCache>();
        cache.Setup(c => c.GetConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var services = new ServiceCollection()
            .AddSingleton(cache.Object)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        var http = new DefaultHttpContext();
        http.SetTenantContext(new TenantContext(Guid.CreateVersion7(), "acme", "Acme", true, false));
        var built = false;

        await ReportReadAudit.WriteAsync(
            services,
            http,
            "POST /api/v4/reports/run",
            _ =>
            {
                built = true;
                return new();
            },
            CancellationToken.None);

        built.Should().BeFalse();
    }
}

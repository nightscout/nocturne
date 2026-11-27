using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Reports;
using Nocturne.Core.Contracts.Audit;
using Npgsql;

namespace Nocturne.API.Tests.Reports;

[Trait("Category", "Unit")]
public class ReportReadAuditTests
{
    public static TheoryData<Exception> UnreadableSettings => new()
    {
        new NpgsqlException("connection refused"),
        new RetryLimitExceededException("retries exhausted", new NpgsqlException("connection refused")),
    };

    public static TheoryData<Exception> Defects => new()
    {
        new InvalidOperationException("no tenant"),
        new NullReferenceException(),
        new TimeoutException(),
    };

    private static Task<bool> WriteWhenTheSettingThrowsAsync(Exception failure, Action onBuilt)
    {
        var cache = new Mock<ITenantAuditConfigCache>();
        cache.Setup(c => c.GetConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var services = new ServiceCollection()
            .AddSingleton(cache.Object)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();

        return ReportReadAudit.WriteAsync(
            services,
            Guid.CreateVersion7(),
            "POST /api/v4/reports/run",
            _ =>
            {
                onBuilt();
                return new();
            },
            CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(UnreadableSettings))]
    public async Task WriteAsync_WhenTheAuditSettingCannotBeRead_SkipsTheRowAndTheRun(Exception failure)
    {
        var built = false;

        var audited = await WriteWhenTheSettingThrowsAsync(failure, () => built = true);

        audited.Should().BeFalse();
        built.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Defects))]
    public async Task WriteAsync_WhenTheSettingReadHitsADefect_ItReachesTheCaller(Exception defect)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => WriteWhenTheSettingThrowsAsync(defect, () => { }));

        thrown.Should().BeSameAs(defect);
    }
}

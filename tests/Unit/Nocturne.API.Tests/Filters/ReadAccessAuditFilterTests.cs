using System.Collections;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Filters;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Tests.Filters;

public class ReadAccessAuditFilterTests
{
    private static readonly Guid TestTenantId = Guid.CreateVersion7();
    private static readonly TenantContext TestTenantContext = new(TestTenantId, "test", "Test Tenant", true);

    private readonly Mock<ITenantAuditConfigCache> _configCache = new();
    private readonly Mock<IAuditContext> _auditContext = new();
    private readonly Mock<IDbContextFactory<NocturneDbContext>> _contextFactory = new();
    private readonly ILogger<ReadAccessAuditFilter> _logger = NullLogger<ReadAccessAuditFilter>.Instance;

    private ReadAccessAuditFilter CreateFilter() =>
        new(_configCache.Object, _auditContext.Object, _contextFactory.Object, _logger);

    private static (ResultExecutingContext executingContext, ResultExecutionDelegate next) CreateContexts(
        string path,
        string method,
        IActionResult? result = null,
        int statusCode = 200,
        string? queryString = null,
        TenantContext? tenantContext = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Request.Method = method;
        if (queryString != null)
            httpContext.Request.QueryString = new QueryString(queryString);
        httpContext.Response.StatusCode = statusCode;

        if (tenantContext != null)
            httpContext.Items["TenantContext"] = tenantContext;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            result ?? new OkResult(),
            controller: null!);

        ResultExecutionDelegate next = () =>
        {
            var executedContext = new ResultExecutedContext(
                actionContext,
                new List<IFilterMetadata>(),
                result ?? new OkResult(),
                controller: null!);
            return Task.FromResult(executedContext);
        };

        return (executingContext, next);
    }

    [Fact]
    public async Task OnResultExecutionAsync_NonV4Endpoint_DoesNotLog()
    {
        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v1/entries", "GET", tenantContext: TestTenantContext);

        await filter.OnResultExecutionAsync(ctx, next);

        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnResultExecutionAsync_NonGetRequest_DoesNotLog()
    {
        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v4/entries", "POST", tenantContext: TestTenantContext);

        await filter.OnResultExecutionAsync(ctx, next);

        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnResultExecutionAsync_ReadAuditDisabled_DoesNotLog()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TestTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(ReadAuditEnabled: false, null, null));

        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v4/entries", "GET", tenantContext: TestTenantContext);

        await filter.OnResultExecutionAsync(ctx, next);

        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnResultExecutionAsync_Status401_DoesNotLog()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TestTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(ReadAuditEnabled: true, null, null));

        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v4/entries", "GET",
            statusCode: 401, tenantContext: TestTenantContext);

        await filter.OnResultExecutionAsync(ctx, next);

        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnResultExecutionAsync_Status403_DoesNotLog()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TestTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(ReadAuditEnabled: true, null, null));

        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v4/entries", "GET",
            statusCode: 403, tenantContext: TestTenantContext);

        await filter.OnResultExecutionAsync(ctx, next);

        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void OnResultExecutionAsync_PaginatedResponse_ExtractsCountAndType()
    {
        var paginated = new PaginatedResponse<SensorGlucoseEntity>
        {
            Data = Enumerable.Range(0, 5).Select(_ => new SensorGlucoseEntity()).ToList(),
            Pagination = new PaginationInfo(100, 0, 5),
        };

        var (recordCount, entityType) = ReadAccessAuditFilter.ExtractResultMetadata(new ObjectResult(paginated));

        recordCount.Should().Be(5);
        entityType.Should().Be("SensorGlucoseEntity");
    }

    [Fact]
    public void OnResultExecutionAsync_SingleObject_CountIsOne()
    {
        var single = new SensorGlucoseEntity();

        var (recordCount, entityType) = ReadAccessAuditFilter.ExtractResultMetadata(new ObjectResult(single));

        recordCount.Should().Be(1);
        entityType.Should().Be("SensorGlucoseEntity");
    }

    [Fact]
    public async Task OnResultExecutionAsync_404Response_IsLogged()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TestTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(ReadAuditEnabled: true, null, null));
        _auditContext.Setup(a => a.AuthType).Returns("Bearer");

        // Use a semaphore to observe the fire-and-forget DB write completing.
        var saveCompleted = new SemaphoreSlim(0, 1);
        var mockDbContext = CreateMockDbContext();
        mockDbContext
            .Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => saveCompleted.Release())
            .ReturnsAsync(1);
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockDbContext.Object);

        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v4/entries/123", "GET",
            statusCode: 404, tenantContext: TestTenantContext);

        await filter.OnResultExecutionAsync(ctx, next);

        // Wait for the fire-and-forget write with a generous timeout.
        var completed = await saveCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        completed.Should().BeTrue("the fire-and-forget audit write should complete");

        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SanitizeQueryParameters_WhitelistedOnly()
    {
        var queryString = "?from=2024-01-01&to=2024-12-31&secret=abc123&limit=100";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);

        var result = ReadAccessAuditFilter.SanitizeQueryParameters(httpContext.Request.Query);

        result.Should().NotBeNull();
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(result!);
        dict.Should().ContainKey("from").WhoseValue.Should().Be("2024-01-01");
        dict.Should().ContainKey("to").WhoseValue.Should().Be("2024-12-31");
        dict.Should().ContainKey("limit").WhoseValue.Should().Be("100");
        dict.Should().ContainKey("secret").WhoseValue.Should().Be("[filtered]");
    }

    [Fact]
    public async Task OnResultExecutionAsync_DbException_SwallowedAndWarned()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TestTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(ReadAuditEnabled: true, null, null));
        _auditContext.Setup(a => a.AuthType).Returns("Bearer");

        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB is down"));

        var filter = CreateFilter();
        var (ctx, next) = CreateContexts("/api/v4/entries", "GET",
            tenantContext: TestTenantContext);

        // Should not throw
        var act = () => filter.OnResultExecutionAsync(ctx, next);
        await act.Should().NotThrowAsync();
    }

    #region ExtractResultMetadata Tests

    [Fact]
    public void ExtractResultMetadata_NullResult_ReturnsNulls()
    {
        var (count, type) = ReadAccessAuditFilter.ExtractResultMetadata(new OkResult());
        count.Should().BeNull();
        type.Should().BeNull();
    }

    [Fact]
    public void ExtractResultMetadata_ObjectResultWithNull_ReturnsNulls()
    {
        var (count, type) = ReadAccessAuditFilter.ExtractResultMetadata(new ObjectResult(null));
        count.Should().BeNull();
        type.Should().BeNull();
    }

    [Fact]
    public void ExtractResultMetadata_Collection_ReturnsCountAndType()
    {
        var list = new List<ApsSnapshotEntity> { new(), new(), new() };
        var (count, type) = ReadAccessAuditFilter.ExtractResultMetadata(new ObjectResult(list));
        count.Should().Be(3);
        type.Should().Be("ApsSnapshotEntity");
    }

    #endregion

    #region SanitizeQueryParameters Tests

    [Fact]
    public void SanitizeQueryParameters_EmptyQuery_ReturnsNull()
    {
        var httpContext = new DefaultHttpContext();
        var result = ReadAccessAuditFilter.SanitizeQueryParameters(httpContext.Request.Query);
        result.Should().BeNull();
    }

    [Fact]
    public void SanitizeQueryParameters_AllWhitelisted_AllPresent()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?from=a&to=b&limit=10&offset=0&sort=asc&device=pump&source=api");
        var result = ReadAccessAuditFilter.SanitizeQueryParameters(httpContext.Request.Query);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(result!);
        dict!["from"].Should().Be("a");
        dict["to"].Should().Be("b");
        dict["limit"].Should().Be("10");
        dict["offset"].Should().Be("0");
        dict["sort"].Should().Be("asc");
        dict["device"].Should().Be("pump");
        dict["source"].Should().Be("api");
    }

    #endregion

    private static Mock<NocturneDbContext> CreateMockDbContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var mock = new Mock<NocturneDbContext>(options) { CallBase = true };
        return mock;
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// A rejected upload is a whole record of any date that the 24-hour clamp's row level security does
/// not cover, so only the owner or a tenant.settings holder reads or clears them, and never a
/// history-clamped caller.
/// </summary>
[Trait("Category", "Unit")]
public class RejectedUploadsControllerTests
{
    private readonly Mock<IRejectedUploadStore> _store = new();
    private readonly Mock<IRejectedUploadRecorder> _recorder = new();

    [Fact]
    public void Every_action_requires_tenant_settings()
    {
        typeof(RejectedUploadsController).GetCustomAttribute<RequireScopeAttribute>()!
            .Scopes.Should().Equal(Scope.TenantSettings);
    }

    [Fact]
    public async Task A_history_clamped_caller_cannot_read_them()
    {
        var result = await Controller(clamped: true).GetRejectedUploads();

        result.Result.Should().BeOfType<ForbidResult>();
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_history_clamped_caller_cannot_clear_them()
    {
        var result = await Controller(clamped: true).ClearRejectedUploads(new ClearRejectedUploadsRequest([Guid.NewGuid()]));

        result.Result.Should().BeOfType<ForbidResult>();
        _recorder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_page_is_at_most_a_hundred()
    {
        _store.Setup(s => s.GetAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaginatedResponse<RejectedUpload>());

        await Controller(clamped: false).GetRejectedUploads(limit: 5000);

        _store.Verify(s => s.GetAsync(100, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clearing_removes_only_the_named_uploads()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _recorder.Setup(r => r.ClearAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var result = await Controller(clamped: false).ClearRejectedUploads(new ClearRejectedUploadsRequest(ids));

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(2);
        _recorder.Verify(r => r.ClearAsync(
            It.Is<IReadOnlyCollection<Guid>>(named => named.SequenceEqual(ids)), It.IsAny<CancellationToken>()), Times.Once);
    }

    private RejectedUploadsController Controller(bool clamped)
    {
        var reads = new Mock<ICategoryReadContext>();
        reads.Setup(r => r.IsHistoryClamped).Returns(clamped);
        var services = new ServiceCollection().AddSingleton(reads.Object).BuildServiceProvider();
        return new RejectedUploadsController(_store.Object, _recorder.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
        };
    }
}

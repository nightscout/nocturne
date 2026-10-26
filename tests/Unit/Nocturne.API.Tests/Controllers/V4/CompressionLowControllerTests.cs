using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class CompressionLowControllerTests
{
    private readonly Mock<ICompressionLowService> _service = new();
    private readonly CompressionLowController _sut;

    public CompressionLowControllerTests()
    {
        _sut = new CompressionLowController(_service.Object, Mock.Of<ICompressionLowDetectionService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static List<CompressionLowBatchItem> Items(int count) =>
        Enumerable.Range(0, count).Select(_ => new CompressionLowBatchItem { Id = Guid.NewGuid() }).ToList();

    private async Task AssertRefusedUntouched(CompressionLowBatchRequest request)
    {
        (await _sut.ReviewSuggestions(request))
            .Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(StatusCodes.Status400BadRequest);

        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public Task ReviewSuggestions_RefusesAnEmptyBatch() =>
        AssertRefusedUntouched(new() { Action = CompressionLowBatchAction.Dismiss, Items = [] });

    [Fact]
    public Task ReviewSuggestions_RefusesABatchOverTheCeiling() =>
        AssertRefusedUntouched(new() { Action = CompressionLowBatchAction.Dismiss, Items = Items(201) });

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public Task ReviewSuggestions_RefusesAMissingOrUnknownAction(int? action) =>
        AssertRefusedUntouched(new() { Action = (CompressionLowBatchAction?)action, Items = Items(1) });

    [Fact]
    public async Task ReviewSuggestions_AcceptsABatchAtTheCeiling()
    {
        var items = Items(200);

        (await _sut.ReviewSuggestions(new() { Action = CompressionLowBatchAction.Delete, Items = items }))
            .Should()
            .BeOfType<NoContentResult>();

        _service.Verify(s => s.ApplyBatchAsync(
            CompressionLowBatchAction.Delete, items, It.IsAny<CancellationToken>()), Times.Once);
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// The entries <c>history/{lastModified}</c> endpoint reads the modified-since page and advances the
/// AAPS cursor from the page's cursor, which can pass every delivered entry's stamp when the page
/// withheld rows.
/// </summary>
[Trait("Category", "Unit")]
public class EntriesHistoryCursorTests
{
    [Fact]
    public async Task GetEntryHistory_ReadsTheModifiedSincePage_AndSetsItsCursor()
    {
        const long cursor = 1_773_144_000_000;
        const long pageCursor = cursor + 60_000;
        var entryService = new Mock<IEntryService>();
        entryService
            .Setup(s => s.GetEntriesModifiedSinceAsync(cursor, 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModifiedSincePage<Entry>([], pageCursor));

        var controller = new EntriesController(
            Mock.Of<IDocumentProcessingService>(),
            entryService.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.GetEntryHistory(cursor);

        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{pageCursor}\"");
    }
}

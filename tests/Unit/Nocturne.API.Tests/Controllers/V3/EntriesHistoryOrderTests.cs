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
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// The entries <c>history/{lastModified}</c> endpoint must page oldest-first so a backlog
/// larger than one page advances the AAPS cursor forward record by record. Newest-first paging
/// (reverseResults: false) sets the cursor to the newest of the first page and silently skips
/// every older unsynced entry.
/// </summary>
[Trait("Category", "Unit")]
public class EntriesHistoryOrderTests
{
    [Fact]
    public async Task GetEntryHistory_PagesAscending()
    {
        var entryService = new Mock<IEntryService>();
        entryService
            .Setup(s => s.GetEntriesWithAdvancedFilterAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Entry>());

        var controller = new EntriesController(
            Mock.Of<IDocumentProcessingService>(),
            entryService.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.GetEntryHistory(0);

        entryService.Verify(s => s.GetEntriesWithAdvancedFilterAsync(
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            true, // reverseResults -> ascending
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

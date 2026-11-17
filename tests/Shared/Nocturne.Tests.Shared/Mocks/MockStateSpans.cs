using Moq;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.Tests.Shared.Mocks;

public static class MockStateSpans
{
    /// <summary>A store with no state spans, answering a read by category as the repository does.</summary>
    public static Mock<IStateSpanRepository> Empty()
    {
        var spans = new Mock<IStateSpanRepository>();
        spans
            .Setup(s => s.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<StateSpanCategory> categories, DateTime? _, DateTime? _, CancellationToken _) =>
                categories.ToDictionary(c => c, _ => new List<StateSpan>()));
        return spans;
    }
}

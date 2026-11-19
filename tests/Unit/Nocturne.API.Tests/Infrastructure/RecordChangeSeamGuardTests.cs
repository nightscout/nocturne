using Nocturne.Core.Contracts.Events;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A V4 write reaches its subscribers only through <see cref="IRecordChangePublisher"/>: no per-type
/// broadcaster port survives under <c>src</c>, and in Infrastructure only the publisher knows the
/// subscribers, so no repository can call one past the stage order.
/// </summary>
[Trait("Category", "Unit")]
public class RecordChangeSeamGuardTests
{
    private const string Publisher = "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/RecordChangePublisher.cs";

    [Fact]
    public void No_source_names_the_per_type_broadcaster_port()
    {
        RepoSources.Src
            .Where(file => file.Text.Contains("IV4RecordBroadcaster", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Should().BeEmpty("a V4 write announces through IRecordChangePublisher");
    }

    [Fact]
    public void Only_the_publisher_references_the_subscribers_in_infrastructure()
    {
        var referencing = RepoSources.Under("src/Infrastructure/Nocturne.Infrastructure.Data")
            .Where(file => file.Text.Contains(nameof(IRecordChangeSubscriber), StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        referencing.Should().Equal([Publisher], "subscribers run only in the publisher's stage order");
    }
}

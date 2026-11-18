using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// <see cref="SourceFilter.Parts{TEntity}"/> evaluated in memory: each row <see cref="SourceFilter.For{TEntity}"/>
/// holds is in exactly one part, and no other row is in any.
/// </summary>
[Trait("Category", "Unit")]
public class SourceFilterTests
{
    private const string Source = "loop-connector";

    public static TheoryData<string?, string?> Handles() => new()
    {
        { Source, Source },
        { Source, null },
        { Source, "phone" },
        { null, Source },
        { "aaa-connector", Source },
        { "zzz-connector", Source },
        { null, null },
        { "aaa-connector", "phone" },
        { null, "phone" },
    };

    [Theory]
    [MemberData(nameof(Handles))]
    public void ARowIsInOnePartExactlyWhenEitherHandleNamesTheSource(string? dataSource, string? device)
    {
        var row = new BolusEntity { DataSource = dataSource, Device = device };

        var parts = SourceFilter.Parts<BolusEntity>(Source).Count(part => part.Compile()(row));

        parts.Should().Be(SourceFilter.For<BolusEntity>(Source).Compile()(row) ? 1 : 0);
    }
}

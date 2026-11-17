using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// A stored category reads back only as the <see cref="StateSpanCategory"/> member it names.
/// </summary>
[Trait("Category", "Unit")]
public class StateSpanMapperCategoryTests
{
    public static TheoryData<StateSpanCategory> Categories() => [.. Enum.GetValues<StateSpanCategory>()];

    [Theory]
    [MemberData(nameof(Categories))]
    public void EveryCategoryReadsBackAsItWasStored(StateSpanCategory category)
    {
        var entity = StateSpanMapper.ToEntity(new StateSpan { Category = category, StartTimestamp = DateTime.UtcNow });

        StateSpanMapper.ToDomainModel(entity).Category.Should().Be(category);
    }

    [Theory]
    [InlineData("TempBasal")]
    [InlineData("BasalDelivery")]
    [InlineData("Sleep")]
    [InlineData("pumpmode")]
    [InlineData("0")]
    [InlineData("")]
    public void AValueThatNamesNoCategoryThrows(string stored)
    {
        var read = () => StateSpanMapper.ToDomainModel(new StateSpanEntity { Category = stored });

        read.Should().Throw<InvalidOperationException>().WithMessage($"*'{stored}'*");
    }
}

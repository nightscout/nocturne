using Nocturne.API.Authorization;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Unit.Services.Realtime;

public class StateSpanBroadcastTests
{
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();

    private static object? GetProp(object payload, string name)
        => payload.GetType().GetProperty(name)!.GetValue(payload);

    private static bool IsStateSpanEvent(object payload, string category) =>
        (string)GetProp(payload, "colName")! == category
        && (string)GetProp(payload, "recordType")! == StateSpanBroadcast.RecordType;

    [Theory]
    [InlineData(StateSpanCategory.Override, RealtimeCategories.Care)]
    [InlineData(StateSpanCategory.TemporaryTarget, RealtimeCategories.Care)]
    [InlineData(StateSpanCategory.PumpMode, RealtimeCategories.Device)]
    [InlineData(StateSpanCategory.DataExclusion, RealtimeCategories.Glucose)]
    [InlineData(StateSpanCategory.Profile, RealtimeCategories.Therapy)]
    public void CategoryOf_IsTheV4CategoryGovernedByTheSpanCategorysReadScope(StateSpanCategory category, string expected)
    {
        StateSpanBroadcast.CategoryOf(category).Should().Be(expected);
        RealtimeCategories.GoverningScopes[expected].Should().Be(StateSpanReadScopeGuard.RequiredReadScope(category));
    }

    [Fact]
    public void CategoryOf_EverySpanCategoryIsReadByTheScopeThatReadsItsBroadcast()
    {
        foreach (var category in Enum.GetValues<StateSpanCategory>())
        {
            if (StateSpanBroadcast.CategoryOf(category) is not { } broadcast)
                continue;

            RealtimeCategories.GoverningScopes[broadcast].Should().Be(
                StateSpanReadScopeGuard.RequiredReadScope(category), "a broadcast reaches every subscriber of {0}", broadcast);
        }
    }

    [Fact]
    public async Task StateSpanCreatedAsync_BroadcastsTheSpanAsACreate()
    {
        var span = new StateSpan { Id = "span-1", Category = StateSpanCategory.Override };

        await _broadcast.Object.StateSpanCreatedAsync(span);

        _broadcast.Verify(b => b.BroadcastStorageCreateAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => IsStateSpanEvent(p, RealtimeCategories.Care) && ReferenceEquals(GetProp(p, "doc"), span))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StateSpanUpdatedAsync_WithinOneBroadcastCategory_BroadcastsOnlyTheUpdate()
    {
        var before = new StateSpan { Id = "span-1", Category = StateSpanCategory.Override };
        var after = new StateSpan { Id = "span-1", Category = StateSpanCategory.TemporaryTarget };

        await _broadcast.Object.StateSpanUpdatedAsync(before, after);

        _broadcast.Verify(b => b.BroadcastStorageUpdateAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => IsStateSpanEvent(p, RealtimeCategories.Care) && ReferenceEquals(GetProp(p, "doc"), after))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StateSpanUpdatedAsync_MovedToAnotherBroadcastCategory_DeletesItFromTheOneItLeft()
    {
        var before = new StateSpan { Id = "span-1", Category = StateSpanCategory.PumpMode };
        var after = new StateSpan { Id = "span-1", Category = StateSpanCategory.Override };

        await _broadcast.Object.StateSpanUpdatedAsync(before, after);

        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync(
            RealtimeCategories.Device,
            It.Is<object>(p => IsStateSpanEvent(p, RealtimeCategories.Device) && (string)GetProp(p, "id")! == "span-1")),
            Times.Once);
        _broadcast.Verify(b => b.BroadcastStorageUpdateAsync(
            RealtimeCategories.Care, It.Is<object>(p => IsStateSpanEvent(p, RealtimeCategories.Care))),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StateSpanDeletedAsync_BroadcastsTheSpansId()
    {
        var span = new StateSpan { Id = "span-1", Category = StateSpanCategory.TemporaryTarget };

        await _broadcast.Object.StateSpanDeletedAsync(span);

        _broadcast.Verify(b => b.BroadcastStorageDeleteAsync(
            RealtimeCategories.Care,
            It.Is<object>(p => IsStateSpanEvent(p, RealtimeCategories.Care) && (string)GetProp(p, "id")! == "span-1")),
            Times.Once);
        _broadcast.VerifyNoOtherCalls();
    }
}

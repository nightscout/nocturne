using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class EnumWireNamesTests
{
    [Fact]
    public void ChannelType_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<ChannelType>();

    [Fact]
    public void AlertChannelState_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<AlertChannelState>();

    [Fact]
    public void GoogleHealthSyncPhase_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<GoogleHealthSyncPhase>();

    [Fact]
    public void ExcursionCloseReason_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<ExcursionCloseReason>();

    [Theory]
    [InlineData(ExcursionCloseReason.Hysteresis, "hysteresis")]
    [InlineData(ExcursionCloseReason.AutoResolve, "auto")]
    [InlineData(ExcursionCloseReason.Manual, "manual")]
    [InlineData(ExcursionCloseReason.RuleDisabled, "rule-disabled")]
    [InlineData(ExcursionCloseReason.HoldBound, "hold-bound")]
    public void A_close_reason_is_stored_as_the_string_old_rows_carry(ExcursionCloseReason reason, string stored)
    {
        reason.ToWireString().Should().Be(stored);
    }

    [Fact]
    public void Every_close_reason_has_a_pinned_stored_string()
    {
        Enum.GetValues<ExcursionCloseReason>().Should().HaveCount(5);
    }

    [Fact]
    public void A_close_reason_outside_the_enum_reads_as_its_number()
    {
        ((ExcursionCloseReason)99).ToWireString().Should().Be("99");
    }

    private static void ReadsAsTheSerialiserWritesIt<T>() where T : struct, Enum
    {
        Enum.GetValues<T>().Should().NotBeEmpty();

        foreach (var value in Enum.GetValues<T>())
            EnumWireNames<T>.Of(value).Should().Be(JsonSerializer.Serialize(value).Trim('"'), "{0}", value);
    }
}

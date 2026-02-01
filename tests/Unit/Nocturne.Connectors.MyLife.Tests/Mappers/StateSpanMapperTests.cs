using FluentAssertions;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Mappers;

public class StateSpanMapperTests
{
    private readonly MyLifeEventProcessor _processor = new();

    private static MyLifeEvent CreateBasalRateEvent(
        long eventDateTime,
        double rate,
        bool isTempBasalRate = false)
    {
        var info = isTempBasalRate
            ? $"{{\"BasalRate\": {rate}, \"IsTempBasalRate\": true}}"
            : $"{{\"BasalRate\": {rate}}}";

        return new MyLifeEvent
        {
            EventTypeId = 17, // BasalRate
            EventDateTime = eventDateTime,
            InformationFromDevice = info,
            PatientId = "test-patient",
            DeviceId = "test-device",
            CRC32Checksum = 12345
        };
    }

    private static MyLifeEvent CreateTempBasalEvent(
        long eventDateTime,
        double rate,
        double? minutes = null,
        double? percent = null)
    {
        var parts = new List<string> { $"\"ValueInUperH\": {rate}" };
        if (minutes.HasValue) parts.Add($"\"Minutes\": {minutes.Value}");
        if (percent.HasValue) parts.Add($"\"Percentage\": {percent.Value}");

        return new MyLifeEvent
        {
            EventTypeId = 4, // TempBasal
            EventDateTime = eventDateTime,
            InformationFromDevice = "{" + string.Join(", ", parts) + "}",
            PatientId = "test-patient",
            DeviceId = "test-device",
            CRC32Checksum = 12346
        };
    }

    private static MyLifeEvent CreateBasalAmountEvent(
        long eventDateTime,
        double insulin)
    {
        return new MyLifeEvent
        {
            EventTypeId = 22, // Basal
            EventDateTime = eventDateTime,
            Value = insulin.ToString(),
            PatientId = "test-patient",
            DeviceId = "test-device",
            CRC32Checksum = 12347
        };
    }

    // Convert DateTime to MyLife ticks (Unix milliseconds * 10_000)
    private static long ToMyLifeTicks(DateTime dt)
    {
        return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds() * 10_000;
    }

    [Fact]
    public void MapStateSpans_BasalRateEvent_CreatesBasalDeliveryStateSpan()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 1.5)
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(1);
        var span = stateSpans[0];
        span.Category.Should().Be(StateSpanCategory.BasalDelivery);
        span.State.Should().Be(BasalDeliveryState.Active.ToString());
        span.Metadata!["rate"].Should().Be(1.5);
        span.Metadata["origin"].Should().Be(BasalDeliveryOrigin.Scheduled.ToString());
        span.Source.Should().Be("mylife-connector");
    }

    [Fact]
    public void MapStateSpans_TempBasalRateEvent_SetsOriginToAlgorithm()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 2.0, isTempBasalRate: true)
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(1);
        var span = stateSpans[0];
        span.Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Algorithm.ToString());
    }

    [Fact]
    public void MapStateSpans_ZeroRate_SetsOriginToSuspended()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 0)
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(1);
        var span = stateSpans[0];
        span.Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Suspended.ToString());
    }

    [Fact]
    public void MapStateSpans_TempBasalEvent_SetsOriginToManual()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateTempBasalEvent(ToMyLifeTicks(eventTime), 1.5, minutes: 60)
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(1);
        var span = stateSpans[0];
        span.Category.Should().Be(StateSpanCategory.BasalDelivery);
        span.Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Manual.ToString());
        span.Metadata["durationMinutes"].Should().Be(60.0);
    }

    [Fact]
    public void MapStateSpans_TempBasalWithDuration_SetsEndMills()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateTempBasalEvent(ToMyLifeTicks(eventTime), 1.5, minutes: 30)
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(1);
        var span = stateSpans[0];
        span.EndMills.Should().NotBeNull();
        var expectedEnd = new DateTimeOffset(eventTime).ToUnixTimeMilliseconds() + (30 * 60 * 1000);
        span.EndMills.Should().Be(expectedEnd);
    }

    [Fact]
    public void MapStateSpans_ConsecutiveSpans_SetsEndMillsOnPreviousSpans()
    {
        // Arrange
        var time1 = DateTime.UtcNow.AddHours(-3);
        var time2 = DateTime.UtcNow.AddHours(-2);
        var time3 = DateTime.UtcNow.AddHours(-1);

        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(time1), 1.0),
            CreateBasalRateEvent(ToMyLifeTicks(time2), 1.5),
            CreateBasalRateEvent(ToMyLifeTicks(time3), 2.0),
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(3);

        // First span should end when second starts
        stateSpans[0].EndMills.Should().Be(stateSpans[1].StartMills);

        // Second span should end when third starts
        stateSpans[1].EndMills.Should().Be(stateSpans[2].StartMills);

        // Third span (most recent) should be open-ended
        stateSpans[2].EndMills.Should().BeNull();
    }

    [Fact]
    public void MapStateSpans_ConsecutiveSpans_CalculatesInsulinDelivered()
    {
        // Arrange
        var time1 = DateTime.UtcNow.AddHours(-2);
        var time2 = DateTime.UtcNow.AddHours(-1); // 1 hour later

        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(time1), 1.5), // 1.5 U/h for 1 hour = 1.5 U
            CreateBasalRateEvent(ToMyLifeTicks(time2), 2.0),
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans[0].Metadata!.Should().ContainKey("insulinDelivered");
        var insulin = (double)stateSpans[0].Metadata["insulinDelivered"];
        insulin.Should().BeApproximately(1.5, 0.01); // 1.5 U/h * 1 hour = 1.5 U
    }

    [Fact]
    public void MapStateSpans_BasalAmountEvent_CreatesStateSpanWithScheduledOrigin()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalAmountEvent(ToMyLifeTicks(eventTime), 1.0)
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(1);
        var span = stateSpans[0];
        span.Category.Should().Be(StateSpanCategory.BasalDelivery);
        span.Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Scheduled.ToString());
    }

    [Fact]
    public void MapStateSpans_DeletedEvents_AreIgnored()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            new MyLifeEvent
            {
                EventTypeId = 17,
                EventDateTime = ToMyLifeTicks(eventTime),
                InformationFromDevice = "{\"BasalRate\": 1.5}",
                Deleted = true,
                PatientId = "test-patient",
                DeviceId = "test-device",
                CRC32Checksum = 12345
            }
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().BeEmpty();
    }

    [Fact]
    public void MapStateSpans_MixedEventTypes_ProcessesAllBasalEvents()
    {
        // Arrange
        var time1 = DateTime.UtcNow.AddHours(-3);
        var time2 = DateTime.UtcNow.AddHours(-2);
        var time3 = DateTime.UtcNow.AddHours(-1);

        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(time1), 1.0),
            CreateTempBasalEvent(ToMyLifeTicks(time2), 2.0, minutes: 30),
            CreateBasalAmountEvent(ToMyLifeTicks(time3), 0.5),
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(3);
        stateSpans[0].Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Scheduled.ToString());
        stateSpans[1].Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Manual.ToString());
        stateSpans[2].Metadata!["origin"].Should().Be(BasalDeliveryOrigin.Scheduled.ToString());
    }

    [Fact]
    public void MapStateSpans_EventsNotInOrder_SortsBeforeProcessing()
    {
        // Arrange - events not in chronological order
        var time1 = DateTime.UtcNow.AddHours(-3);
        var time2 = DateTime.UtcNow.AddHours(-2);
        var time3 = DateTime.UtcNow.AddHours(-1);

        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(time3), 3.0), // Latest first
            CreateBasalRateEvent(ToMyLifeTicks(time1), 1.0), // Earliest
            CreateBasalRateEvent(ToMyLifeTicks(time2), 2.0), // Middle
        };

        // Act
        var stateSpans = _processor.MapStateSpans(events, false, 0).ToList();

        // Assert
        stateSpans.Should().HaveCount(3);

        // Should be sorted by StartMills
        stateSpans[0].StartMills.Should().BeLessThan(stateSpans[1].StartMills);
        stateSpans[1].StartMills.Should().BeLessThan(stateSpans[2].StartMills);

        // End times should chain correctly
        stateSpans[0].EndMills.Should().Be(stateSpans[1].StartMills);
        stateSpans[1].EndMills.Should().Be(stateSpans[2].StartMills);
    }
}

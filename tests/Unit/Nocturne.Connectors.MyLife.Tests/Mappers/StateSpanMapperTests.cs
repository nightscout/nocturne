using FluentAssertions;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Mappers;

public class StateSpanMapperTests
{
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
            EventTypeId = MyLifeEventType.BasalRate,
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
            EventTypeId = MyLifeEventType.TempBasal,
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
            EventTypeId = MyLifeEventType.Basal,
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
    public void MapTempBasals_BasalRateEvent_CreatesTempBasalWithScheduledOrigin()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 1.5)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Rate.Should().Be(1.5);
        record.Origin.Should().Be(TempBasalOrigin.Scheduled);
        record.DataSource.Should().Be("mylife-connector");
    }

    [Fact]
    public void MapTempBasals_TempBasalRateEvent_SetsOriginToAlgorithm()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 2.0, isTempBasalRate: true)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Origin.Should().Be(TempBasalOrigin.Algorithm);
    }

    [Fact]
    public void MapTempBasals_ZeroRate_SetsOriginToSuspended()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 0)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Origin.Should().Be(TempBasalOrigin.Suspended);
    }

    [Fact]
    public void MapTempBasals_TempBasalEvent_SetsOriginToManual()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateTempBasalEvent(ToMyLifeTicks(eventTime), 1.5, minutes: 60)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Rate.Should().Be(1.5);
        record.Origin.Should().Be(TempBasalOrigin.Manual);
    }

    [Fact]
    public void MapTempBasals_TempBasalWithDuration_SetsEndMills()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateTempBasalEvent(ToMyLifeTicks(eventTime), 1.5, minutes: 30)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.EndMills.Should().NotBeNull();
        var expectedEnd = new DateTimeOffset(eventTime).ToUnixTimeMilliseconds() + (30 * 60 * 1000);
        record.EndMills.Should().Be(expectedEnd);
    }

    [Fact]
    public void MapTempBasals_ConsecutiveRecords_SetsEndMillsOnPreviousRecords()
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
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(3);

        // First record should end when second starts
        tempBasals[0].EndMills.Should().Be(tempBasals[1].StartMills);

        // Second record should end when third starts
        tempBasals[1].EndMills.Should().Be(tempBasals[2].StartMills);

        // Third record (most recent) should be open-ended
        tempBasals[2].EndMills.Should().BeNull();
    }

    [Fact]
    public void MapTempBasals_BasalAmountEvent_CreatesTempBasalWithScheduledOrigin()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalAmountEvent(ToMyLifeTicks(eventTime), 1.0)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Rate.Should().Be(1.0);
        record.Origin.Should().Be(TempBasalOrigin.Scheduled);
    }

    [Fact]
    public void MapTempBasals_DeletedEvents_AreIgnored()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            new MyLifeEvent
            {
                EventTypeId = MyLifeEventType.BasalRate,
                EventDateTime = ToMyLifeTicks(eventTime),
                InformationFromDevice = "{\"BasalRate\": 1.5}",
                Deleted = true,
                PatientId = "test-patient",
                DeviceId = "test-device",
                CRC32Checksum = 12345
            }
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().BeEmpty();
    }

    [Fact]
    public void MapTempBasals_MixedEventTypes_SkipsBasalWhenBasalRatePresent()
    {
        // Arrange — when BasalRate events exist, Basal (amount) events are
        // redundant delivery confirmations and should be skipped.
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
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert — Basal event skipped because BasalRate events are present
        tempBasals.Should().HaveCount(2);
        tempBasals[0].Origin.Should().Be(TempBasalOrigin.Scheduled);
        tempBasals[1].Origin.Should().Be(TempBasalOrigin.Manual);
    }

    [Fact]
    public void MapTempBasals_EventsNotInOrder_SortsBeforeProcessing()
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
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(3);

        // Should be sorted by StartMills
        tempBasals[0].StartMills.Should().BeLessThan(tempBasals[1].StartMills);
        tempBasals[1].StartMills.Should().BeLessThan(tempBasals[2].StartMills);

        // End times should chain correctly
        tempBasals[0].EndMills.Should().Be(tempBasals[1].StartMills);
        tempBasals[1].EndMills.Should().Be(tempBasals[2].StartMills);
    }

    [Fact]
    public void MapTempBasals_TempBasalRecords_HaveRequiredFields()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalRateEvent(ToMyLifeTicks(eventTime), 1.5)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Id.Should().NotBe(Guid.Empty);
        record.StartMills.Should().BeGreaterThan(0);
        record.Rate.Should().Be(1.5);
        record.LegacyId.Should().NotBeNullOrEmpty();
        record.DataSource.Should().Be("mylife-connector");
        record.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        record.ModifiedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MapTempBasals_ZeroBasalAmount_SetsOriginToSuspended()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-1);
        var events = new[]
        {
            CreateBasalAmountEvent(ToMyLifeTicks(eventTime), 0)
        };

        // Act
        var tempBasals = MyLifeStateSpanMapper.MapTempBasals(events, false, 0).ToList();

        // Assert
        tempBasals.Should().HaveCount(1);
        var record = tempBasals[0];
        record.Origin.Should().Be(TempBasalOrigin.Suspended);
    }
}

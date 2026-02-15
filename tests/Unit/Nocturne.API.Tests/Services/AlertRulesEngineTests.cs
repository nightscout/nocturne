using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Unit tests for AlertRulesEngine service
/// </summary>
public class AlertRulesEngineTests
{
    private readonly Mock<AlertRuleRepository> _mockAlertRuleRepository;
    private readonly Mock<AlertHistoryRepository> _mockAlertHistoryRepository;
    private readonly Mock<NotificationPreferencesRepository> _mockNotificationPreferencesRepository;
    private readonly Mock<IPredictionService> _mockPredictionService;
    private readonly Mock<IOptions<AlertMonitoringOptions>> _mockOptions;
    private readonly Mock<ILogger<AlertRulesEngine>> _mockLogger;
    private readonly AlertRulesEngine _alertRulesEngine;
    private readonly AlertMonitoringOptions _defaultOptions;

    public AlertRulesEngineTests()
    {
        var db = Nocturne.Tests.Shared.Infrastructure.TestDbContextFactory.CreateInMemoryContext();
        _mockAlertRuleRepository = new Mock<AlertRuleRepository>(db) { CallBase = true };
        _mockAlertHistoryRepository = new Mock<AlertHistoryRepository>(db) { CallBase = true };
        _mockNotificationPreferencesRepository = new Mock<NotificationPreferencesRepository>(db)
        {
            CallBase = true,
        };
        _mockPredictionService = new Mock<IPredictionService>();
        _mockOptions = new Mock<IOptions<AlertMonitoringOptions>>();
        _mockLogger = new Mock<ILogger<AlertRulesEngine>>();

        _defaultOptions = new AlertMonitoringOptions
        {
            AlertCooldownMinutes = 15,
            MaxActiveAlertsPerUser = 10,
            HysteresisPercentage = 0.1,
            DefaultLowThreshold = 70,
            DefaultHighThreshold = 180,
            DefaultUrgentLowThreshold = 55,
            DefaultUrgentHighThreshold = 300,
        };

        _mockOptions.Setup(x => x.Value).Returns(_defaultOptions);

        _alertRulesEngine = new AlertRulesEngine(
            _mockAlertRuleRepository.Object,
            _mockAlertHistoryRepository.Object,
            _mockNotificationPreferencesRepository.Object,
            _mockOptions.Object,
            _mockLogger.Object,
            _mockPredictionService.Object
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EvaluateGlucoseData_WithNoActiveRules_ReturnsEmptyList()
    {
        // Arrange
        var userId = "test-user";
        var glucoseReading = CreateGlucoseEntry(100, DateTime.UtcNow);
        _mockAlertRuleRepository
            .Setup(x => x.GetActiveRulesForUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleEntity>());

        // Act
        var result = await _alertRulesEngine.EvaluateGlucoseData(
            glucoseReading,
            userId,
            CancellationToken.None
        );

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EvaluateGlucoseData_WithUserInQuietHours_ReturnsEmptyList()
    {
        // Arrange
        var userId = "test-user";
        var glucoseReading = CreateGlucoseEntry(100, DateTime.UtcNow);
        var activeRules = new[] { CreateAlertRule(userId) };

        _mockAlertRuleRepository
            .Setup(x => x.GetActiveRulesForUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeRules);

        _mockNotificationPreferencesRepository
            .Setup(x =>
                x.IsUserInQuietHoursAsync(
                    userId,
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(true);

        // Act
        var result = await _alertRulesEngine.EvaluateGlucoseData(
            glucoseReading,
            userId,
            CancellationToken.None
        );

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EvaluateGlucoseData_WithMaxActiveAlertsReached_ReturnsEmptyList()
    {
        // Arrange
        var userId = "test-user";
        var glucoseReading = CreateGlucoseEntry(100, DateTime.UtcNow);
        var activeRules = new[] { CreateAlertRule(userId) };

        _mockAlertRuleRepository
            .Setup(x => x.GetActiveRulesForUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeRules);

        _mockNotificationPreferencesRepository
            .Setup(x =>
                x.IsUserInQuietHoursAsync(
                    userId,
                    It.IsAny<DateTime?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(false);

        _mockAlertHistoryRepository
            .Setup(x => x.GetActiveAlertCountForUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_defaultOptions.MaxActiveAlertsPerUser);

        // Act
        var result = await _alertRulesEngine.EvaluateGlucoseData(
            glucoseReading,
            userId,
            CancellationToken.None
        );

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsAlertConditionMet_WithLowGlucose_ReturnsTrue()
    {
        // Arrange
        var glucoseReading = CreateGlucoseEntry(60, DateTime.UtcNow); // Below low threshold
        var rule = CreateAlertRule("test-user", lowThreshold: 70);

        _mockAlertHistoryRepository
            .Setup(x =>
                x.GetActiveAlertForRuleAndTypeAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((AlertHistoryEntity?)null);

        // Act
        var result = await _alertRulesEngine.IsAlertConditionMet(
            glucoseReading,
            rule,
            CancellationToken.None
        );

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsAlertConditionMet_WithHighGlucose_ReturnsTrue()
    {
        // Arrange
        var glucoseReading = CreateGlucoseEntry(200, DateTime.UtcNow); // Above high threshold
        var rule = CreateAlertRule("test-user", highThreshold: 180);

        _mockAlertHistoryRepository
            .Setup(x =>
                x.GetActiveAlertForRuleAndTypeAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((AlertHistoryEntity?)null);

        // Act
        var result = await _alertRulesEngine.IsAlertConditionMet(
            glucoseReading,
            rule,
            CancellationToken.None
        );

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsAlertConditionMet_WithUrgentLowGlucose_ReturnsTrue()
    {
        // Arrange
        var glucoseReading = CreateGlucoseEntry(45, DateTime.UtcNow); // Below urgent low threshold
        var rule = CreateAlertRule("test-user", urgentLowThreshold: 55);

        _mockAlertHistoryRepository
            .Setup(x =>
                x.GetActiveAlertForRuleAndTypeAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((AlertHistoryEntity?)null);

        // Act
        var result = await _alertRulesEngine.IsAlertConditionMet(
            glucoseReading,
            rule,
            CancellationToken.None
        );

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsAlertConditionMet_WithNormalGlucose_ReturnsFalse()
    {
        // Arrange
        var glucoseReading = CreateGlucoseEntry(100, DateTime.UtcNow); // Normal glucose
        var rule = CreateAlertRule("test-user", lowThreshold: 70, highThreshold: 180);

        // Act
        var result = await _alertRulesEngine.IsAlertConditionMet(
            glucoseReading,
            rule,
            CancellationToken.None
        );

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsAlertConditionMet_WithActiveCooldown_ReturnsFalse()
    {
        // Arrange
        var glucoseReading = CreateGlucoseEntry(60, DateTime.UtcNow); // Below low threshold
        var rule = CreateAlertRule("test-user", lowThreshold: 70);
        var existingAlert = new AlertHistoryEntity
        {
            Id = Guid.NewGuid(),
            UserId = "test-user",
            AlertRuleId = rule.Id,
            AlertType = "Low",
            Status = "ACTIVE",
            TriggerTime = DateTime.UtcNow.AddMinutes(-5), // 5 minutes ago (within cooldown)
        };

        _mockAlertHistoryRepository
            .Setup(x =>
                x.GetActiveAlertForRuleAndTypeAsync(
                    "test-user",
                    rule.Id,
                    "Low",
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(existingAlert);

        // Act
        var result = await _alertRulesEngine.IsAlertConditionMet(
            glucoseReading,
            rule,
            CancellationToken.None
        );

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EvaluateTimeBasedConditions_WithNoTimeRestrictions_ReturnsTrue()
    {
        // Arrange
        var rule = CreateAlertRule("test-user");
        var checkTime = DateTime.UtcNow;

        // Act
        var result = _alertRulesEngine.EvaluateTimeBasedConditions(rule, checkTime);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(9, 0, true)] // 9:00 AM - within active hours
    [InlineData(15, 30, true)] // 3:30 PM - within active hours
    [InlineData(22, 0, true)] // 10:00 PM - end of active hours
    [InlineData(6, 0, false)] // 6:00 AM - before active hours
    [InlineData(23, 0, false)] // 11:00 PM - after active hours
    public void EvaluateTimeBasedConditions_WithActiveHours_ReturnsExpectedResult(
        int hour,
        int minute,
        bool expected
    )
    {
        // Arrange
        var activeHours = """{"StartHour":8,"StartMinute":0,"EndHour":22,"EndMinute":0}""";
        var rule = CreateAlertRule("test-user", activeHours: activeHours);
        var checkTime = new DateTime(2024, 1, 1, hour, minute, 0, DateTimeKind.Utc);

        // Act
        var result = _alertRulesEngine.EvaluateTimeBasedConditions(rule, checkTime);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(DayOfWeek.Monday, true)]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Friday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void EvaluateTimeBasedConditions_WithDaysOfWeek_ReturnsExpectedResult(
        DayOfWeek dayOfWeek,
        bool expected
    )
    {
        // Arrange - Monday(1), Wednesday(3), Friday(5)
        var daysOfWeek = "[1,3,5]";
        var rule = CreateAlertRule("test-user", daysOfWeek: daysOfWeek);

        // Find a date that falls on the specified day of week
        var baseDate = new DateTime(2024, 1, 1); // Monday
        var checkTime = baseDate.AddDays((int)dayOfWeek - (int)baseDate.DayOfWeek);

        // Act
        var result = _alertRulesEngine.EvaluateTimeBasedConditions(rule, checkTime);

        // Assert
        result.Should().Be(expected);
    }

    private static Entry CreateGlucoseEntry(double glucoseValue, DateTime timestamp)
    {
        return new Entry
        {
            Id = Guid.NewGuid().ToString(),
            Sgv = glucoseValue,
            Mgdl = glucoseValue,
            Date = timestamp,
            Type = "sgv",
            Direction = "Flat",
        };
    }

    private static AlertRuleEntity CreateAlertRule(
        string userId,
        decimal? lowThreshold = null,
        decimal? highThreshold = null,
        decimal? urgentLowThreshold = null,
        decimal? urgentHighThreshold = null,
        string? activeHours = null,
        string? daysOfWeek = null
    )
    {
        return new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Rule",
            IsEnabled = true,
            LowThreshold = lowThreshold,
            HighThreshold = highThreshold,
            UrgentLowThreshold = urgentLowThreshold,
            UrgentHighThreshold = urgentHighThreshold,
            ActiveHours = activeHours,
            DaysOfWeek = daysOfWeek,
            NotificationChannels = "[]",
            EscalationDelayMinutes = 15,
            MaxEscalations = 3,
            DefaultSnoozeMinutes = 30,
            MaxSnoozeMinutes = 120,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}

using FluentAssertions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class GlucoseProcessingConfigProviderTests
{
    private readonly Mock<ISettingsRepository> _settingsRepository;
    private readonly GlucoseProcessingConfigProvider _sut;

    public GlucoseProcessingConfigProviderTests()
    {
        _settingsRepository = new Mock<ISettingsRepository>();
        _sut = new GlucoseProcessingConfigProvider(_settingsRepository.Object);
    }

    // --- SetPreferredProcessingAsync ---

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetPreferredProcessing_WhenNoExisting_CreatesNewSetting()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Settings?)null);

        await _sut.SetPreferredProcessingAsync(GlucoseProcessing.Smoothed);

        _settingsRepository.Verify(
            x => x.CreateSettingsAsync(
                It.Is<IEnumerable<Settings>>(s =>
                    s.Single().Key == "preferredGlucoseProcessing" &&
                    (string)s.Single().Value! == "Smoothed" &&
                    s.Single().IsActive),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _settingsRepository.Verify(
            x => x.UpdateSettingsAsync(It.IsAny<string>(), It.IsAny<Settings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetPreferredProcessing_WhenExisting_UpdatesSetting()
    {
        var existing = new Settings { Id = "abc-123", Key = "preferredGlucoseProcessing", Value = "Unsmoothed" };
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await _sut.SetPreferredProcessingAsync(GlucoseProcessing.Smoothed);

        _settingsRepository.Verify(
            x => x.UpdateSettingsAsync("abc-123", It.Is<Settings>(s => (string)s.Value! == "Smoothed"), It.IsAny<CancellationToken>()),
            Times.Once);

        _settingsRepository.Verify(
            x => x.CreateSettingsAsync(It.IsAny<IEnumerable<Settings>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetPreferredProcessing_WithNull_WhenExisting_UpdatesToNull()
    {
        var existing = new Settings { Id = "abc-123", Key = "preferredGlucoseProcessing", Value = "Smoothed" };
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await _sut.SetPreferredProcessingAsync(null);

        _settingsRepository.Verify(
            x => x.UpdateSettingsAsync("abc-123", It.Is<Settings>(s => s.Value == null), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetPreferredProcessing_WithNull_WhenNoExisting_DoesNotCreate()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Settings?)null);

        await _sut.SetPreferredProcessingAsync(null);

        _settingsRepository.Verify(
            x => x.CreateSettingsAsync(It.IsAny<IEnumerable<Settings>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _settingsRepository.Verify(
            x => x.UpdateSettingsAsync(It.IsAny<string>(), It.IsAny<Settings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- SetSourceDefaultsAsync ---

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetSourceDefaults_WhenNoExisting_CreatesNewSetting()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("glucoseProcessingSourceDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Settings?)null);

        var defaults = new List<GlucoseProcessingSourceDefault>
        {
            new() { Match = "Dexcom", Field = "device", Processing = GlucoseProcessing.Smoothed },
        };

        await _sut.SetSourceDefaultsAsync(defaults);

        _settingsRepository.Verify(
            x => x.CreateSettingsAsync(
                It.Is<IEnumerable<Settings>>(s =>
                    s.Single().Key == "glucoseProcessingSourceDefaults" &&
                    ((string)s.Single().Value!).Contains("\"match\":\"Dexcom\"") &&
                    ((string)s.Single().Value!).Contains("\"processing\":") &&
                    s.Single().IsActive),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetSourceDefaults_WhenExisting_UpdatesSetting()
    {
        var existing = new Settings { Id = "def-456", Key = "glucoseProcessingSourceDefaults", Value = "[]" };
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("glucoseProcessingSourceDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var defaults = new List<GlucoseProcessingSourceDefault>
        {
            new() { Match = "Libre", Field = "device", Processing = GlucoseProcessing.Unsmoothed },
        };

        await _sut.SetSourceDefaultsAsync(defaults);

        _settingsRepository.Verify(
            x => x.UpdateSettingsAsync(
                "def-456",
                It.Is<Settings>(s => ((string)s.Value!).Contains("\"match\":\"Libre\"")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _settingsRepository.Verify(
            x => x.CreateSettingsAsync(It.IsAny<IEnumerable<Settings>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetSourceDefaults_WithEmptyList_UpdatesToEmptyArray()
    {
        var existing = new Settings { Id = "ghi-789", Key = "glucoseProcessingSourceDefaults", Value = "[{\"match\":\"x\"}]" };
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("glucoseProcessingSourceDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await _sut.SetSourceDefaultsAsync([]);

        _settingsRepository.Verify(
            x => x.UpdateSettingsAsync(
                "ghi-789",
                It.Is<Settings>(s => (string)s.Value! == "[]"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

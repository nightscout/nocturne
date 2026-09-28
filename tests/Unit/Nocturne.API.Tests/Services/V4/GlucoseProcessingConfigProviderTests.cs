using FluentAssertions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class GlucoseProcessingConfigProviderTests
{
    private readonly Mock<ISettingsRepository> _settingsRepository;
    private readonly GlucoseProcessingConfigProvider _sut;
    private Guid _tenantId = Guid.NewGuid();

    public GlucoseProcessingConfigProviderTests()
    {
        _settingsRepository = new Mock<ISettingsRepository>();
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(x => x.TenantId).Returns(() => _tenantId);
        _sut = new GlucoseProcessingConfigProvider(_settingsRepository.Object, tenantAccessor.Object);
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

    // --- Reads, once per scope ---

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSourceDefaults_ReadsTheSettingOncePerScope()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("glucoseProcessingSourceDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Settings { Key = "glucoseProcessingSourceDefaults", Value = "[]" });

        await _sut.GetSourceDefaultsAsync();
        await _sut.GetSourceDefaultsAsync();

        _settingsRepository.Verify(
            x => x.GetSettingsByKeyAsync("glucoseProcessingSourceDefaults", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetPreferredProcessing_ReadsAnUnsetSettingOncePerScope()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Settings?)null);

        (await _sut.GetPreferredProcessingAsync()).Should().BeNull();
        (await _sut.GetPreferredProcessingAsync()).Should().BeNull();

        _settingsRepository.Verify(
            x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetPreferredProcessing_AfterTheScopeSwitchesTenant_ReadsThatTenantsSetting()
    {
        _settingsRepository
            .SetupSequence(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Settings { Key = "preferredGlucoseProcessing", Value = "Smoothed" })
            .ReturnsAsync(new Settings { Key = "preferredGlucoseProcessing", Value = "Unsmoothed" });

        (await _sut.GetPreferredProcessingAsync()).Should().Be(GlucoseProcessing.Smoothed);
        _tenantId = Guid.NewGuid();

        (await _sut.GetPreferredProcessingAsync()).Should().Be(GlucoseProcessing.Unsmoothed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSourceDefaults_ACallerMutatingTheResult_DoesNotChangeTheCachedRules()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("glucoseProcessingSourceDefaults", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Settings { Key = "glucoseProcessingSourceDefaults", Value = "[]" });

        (await _sut.GetSourceDefaultsAsync()).Add(new GlucoseProcessingSourceDefault { Match = "xDrip", Field = "device", Processing = GlucoseProcessing.Smoothed });

        (await _sut.GetSourceDefaultsAsync()).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetPreferredProcessing_AfterASetInTheSameScope_ReturnsTheNewValue()
    {
        _settingsRepository
            .Setup(x => x.GetSettingsByKeyAsync("preferredGlucoseProcessing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Settings { Id = "abc-123", Key = "preferredGlucoseProcessing", Value = "Unsmoothed" });

        (await _sut.GetPreferredProcessingAsync()).Should().Be(GlucoseProcessing.Unsmoothed);
        await _sut.SetPreferredProcessingAsync(GlucoseProcessing.Smoothed);

        (await _sut.GetPreferredProcessingAsync()).Should().Be(GlucoseProcessing.Smoothed);
    }
}

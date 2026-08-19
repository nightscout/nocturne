using System;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Unit;

public class BaseConnectorConfigurationTests
{
    [Fact]
    public void Validate_WithValidBasicConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new TestConnectorConfiguration
        {
            ConnectSource = ConnectSource.Dexcom, // Using a valid enum value for testing
        };

        // Act & Assert
        config.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_WithInvalidConnectSource_ThrowsArgumentException()
    {
        // Arrange
        var config = new TestConnectorConfiguration
        {
            ConnectSource = (ConnectSource)999, // Invalid enum value
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("Invalid connector source", exception.Message);
    }

    [Fact]
    public void ValidateMessagingConfiguration_WithNegativeMaxRetryAttempts_ThrowsArgumentException()
    {
        // Arrange
        var config = new TestConnectorConfiguration
        {
            ConnectSource = ConnectSource.Dexcom, // Using a valid enum value for testing
            MaxRetryAttempts = -1,
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("MaxRetryAttempts cannot be negative", exception.Message);
    }

    [Fact]
    public void ValidateMessagingConfiguration_WithInvalidBatchSize_ThrowsArgumentException()
    {
        // Arrange
        var config = new TestConnectorConfiguration
        {
            ConnectSource = ConnectSource.Dexcom, // Using a valid enum value for testing
            BatchSize = 0,
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("BatchSize must be greater than zero", exception.Message);
    }

    [Fact]
    public void DefaultValues_AreSetCorrectly()
    {
        // Arrange & Act
        var config = new TestConnectorConfiguration();

        // Assert
        Assert.Equal(3, config.MaxRetryAttempts);
        Assert.Equal(50, config.BatchSize);
        Assert.Equal(5, config.SyncIntervalMinutes);
    }

    [Fact]
    public void SyncTempBasals_IsEnabledByDefault()
    {
        var config = new TestConnectorConfiguration();

        Assert.True(config.SyncTempBasals);
        Assert.True(config.IsDataTypeEnabled(SyncDataType.TempBasals));
    }

    [Fact]
    public void IsDataTypeEnabled_TempBasals_FollowsToggle()
    {
        var config = new TestConnectorConfiguration { SyncTempBasals = false };

        Assert.False(config.IsDataTypeEnabled(SyncDataType.TempBasals));
    }

    [Fact]
    public void SyncBasalInjections_IsEnabledByDefault()
    {
        var config = new TestConnectorConfiguration();

        Assert.True(config.SyncBasalInjections);
        Assert.True(config.IsDataTypeEnabled(SyncDataType.BasalInjections));
    }

    [Fact]
    public void IsDataTypeEnabled_BasalInjections_FollowsToggle()
    {
        var config = new TestConnectorConfiguration { SyncBasalInjections = false };

        Assert.False(config.IsDataTypeEnabled(SyncDataType.BasalInjections));
    }

    [Fact]
    public void GetEnabledDataTypes_IncludesTempBasals_WhenSupportedAndEnabled()
    {
        var config = new TestConnectorConfiguration();

        var enabled = config.GetEnabledDataTypes([SyncDataType.StateSpans, SyncDataType.TempBasals]);

        Assert.Contains(SyncDataType.TempBasals, enabled);
        // Guards the Glooko refactor: temp basals are no longer dropped when StateSpans is off.
        var stateSpansOff = new TestConnectorConfiguration { SyncStateSpans = false };
        var stillEnabled = stateSpansOff.GetEnabledDataTypes([SyncDataType.StateSpans, SyncDataType.TempBasals]);
        Assert.DoesNotContain(SyncDataType.StateSpans, stillEnabled);
        Assert.Contains(SyncDataType.TempBasals, stillEnabled);
    }
}

/// <summary>
/// Test implementation of BaseConnectorConfiguration
/// </summary>
internal class TestConnectorConfiguration : BaseConnectorConfiguration
{
    protected override void ValidateSourceSpecificConfiguration()
    {
        // No additional validation for test implementation
    }
}

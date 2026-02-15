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

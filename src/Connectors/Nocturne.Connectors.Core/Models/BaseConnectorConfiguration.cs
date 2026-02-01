using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Base implementation of connector configuration with common properties
/// </summary>
public abstract class BaseConnectorConfiguration : IConnectorConfiguration
{
    /// <summary>
    ///     Timezone offset in hours (default 0).
    ///     Can be set via environment variable: CONNECT_{CONNECTORNAME}_TIMEZONE_OFFSET
    ///     or appsettings: {Configuration}:TimezoneOffset
    /// </summary>
    [RuntimeConfigurable("Timezone Offset", "General")]
    [ConfigSchema(Minimum = -12, Maximum = 14)]
    public double TimezoneOffset { get; set; } = 0;

    [Required] public ConnectSource ConnectSource { get; set; }

    /// <summary>
    ///     Whether the connector is enabled and should sync data.
    ///     When disabled, the connector enters standby mode.
    /// </summary>
    [RuntimeConfigurable("Enabled", "General")]
    public bool Enabled { get; set; } = true;

    [RuntimeConfigurable("Max Retry Attempts", "Advanced")]
    [ConfigSchema(Minimum = 0, Maximum = 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    [RuntimeConfigurable("Batch Size", "Advanced")]
    [ConfigSchema(Minimum = 1, Maximum = 500)]
    public int BatchSize { get; set; } = 50;

    [RuntimeConfigurable("Sync Interval (Minutes)", "Sync")]
    [ConfigSchema(Minimum = 1, Maximum = 60)]
    public int SyncIntervalMinutes { get; set; } = 5;

    public virtual void Validate()
    {
        if (!Enum.IsDefined(typeof(ConnectSource), ConnectSource))
            throw new ArgumentException($"Invalid connector source: {ConnectSource}");

        if (MaxRetryAttempts < 0)
            throw new ArgumentException("MaxRetryAttempts cannot be negative");

        if (BatchSize <= 0)
            throw new ArgumentException("BatchSize must be greater than zero");

        ValidateSourceSpecificConfiguration();
    }

    /// <summary>
    ///     Override this method to validate connector-specific configuration
    /// </summary>
    protected abstract void ValidateSourceSpecificConfiguration();

    
}

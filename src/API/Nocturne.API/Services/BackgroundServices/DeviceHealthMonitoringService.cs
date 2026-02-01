using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service for continuous device health monitoring and maintenance alerts
/// </summary>
public class DeviceHealthMonitoringService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DeviceHealthMonitoringService> _logger;
    private readonly DeviceHealthOptions _options;

    /// <summary>
    /// Initializes a new instance of the DeviceHealthMonitoringService
    /// </summary>
    /// <param name="serviceProvider">Service provider for creating scoped services</param>
    /// <param name="logger">Logger</param>
    /// <param name="options">Device health options</param>
    public DeviceHealthMonitoringService(
        IServiceProvider serviceProvider,
        ILogger<DeviceHealthMonitoringService> logger,
        IOptions<DeviceHealthOptions> options
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Execute the background monitoring service
    /// </summary>
    /// <param name="stoppingToken">Stopping token</param>
    /// <returns>Task completion</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Device Health Monitoring Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformHealthCheckAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during device health monitoring");
            }

            // Wait for the next health check interval
            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(_options.HealthCheckIntervalMinutes),
                    stoppingToken
                );
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Device Health Monitoring Service stopped");
    }

    /// <summary>
    /// Perform health check on all registered devices
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task PerformHealthCheckAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var deviceRegistry = scope.ServiceProvider.GetRequiredService<IDeviceRegistryService>();

        var alertEngine = scope.ServiceProvider.GetRequiredService<IDeviceAlertEngine>();

        _logger.LogDebug("Starting device health check cycle");

        try
        {
            // Get all active devices
            var devices = await GetActiveDevicesAsync(dbContext, cancellationToken);

            _logger.LogDebug("Checking health for {DeviceCount} devices", devices.Count);

            // Process devices in batches to avoid overwhelming the system
            var batchSize = 10;
            for (int i = 0; i < devices.Count; i += batchSize)
            {
                var batch = devices.Skip(i).Take(batchSize).ToList();
                await ProcessDeviceBatchAsync(
                    batch,
                    deviceRegistry,

                    alertEngine,
                    cancellationToken
                );
            }

            _logger.LogDebug(
                "Completed device health check cycle for {DeviceCount} devices",
                devices.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during health check cycle");
            throw;
        }
    }

    /// <summary>
    /// Get all active devices that need health monitoring
    /// </summary>
    /// <param name="dbContext">Database context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of device IDs to check</returns>
    private async Task<List<string>> GetActiveDevicesAsync(
        NocturneDbContext dbContext,
        CancellationToken cancellationToken
    )
    {
        return await dbContext
            .DeviceHealth.Where(d =>
                d.Status == DeviceStatusType.Active
                || d.Status == DeviceStatusType.Warning
                || d.Status == DeviceStatusType.Maintenance
            )
            .Select(d => d.DeviceId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Process a batch of devices for health monitoring
    /// </summary>
    /// <param name="deviceIds">List of device IDs to process</param>
    /// <param name="deviceRegistry">Device registry service</param>

    /// <param name="alertEngine">Alert engine service</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task ProcessDeviceBatchAsync(
        List<string> deviceIds,
        IDeviceRegistryService deviceRegistry,

        IDeviceAlertEngine alertEngine,
        CancellationToken cancellationToken
    )
    {
        var tasks = deviceIds.Select(deviceId =>
            ProcessSingleDeviceAsync(
                deviceId,
                deviceRegistry,

                alertEngine,
                cancellationToken
            )
        );

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Process health monitoring for a single device
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="deviceRegistry">Device registry service</param>

    /// <param name="alertEngine">Alert engine service</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task ProcessSingleDeviceAsync(
        string deviceId,
        IDeviceRegistryService deviceRegistry,

        IDeviceAlertEngine alertEngine,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (_options.EnableDebugLogging)
            {
                _logger.LogDebug("Processing device health for device {DeviceId}", deviceId);
            }

            // Get device information
            var device = await deviceRegistry.GetDeviceAsync(deviceId, cancellationToken);
            if (device == null)
            {
                _logger.LogWarning("Device {DeviceId} not found during health check", deviceId);
                return;
            }



            // Generate and process alerts
            var alerts = await alertEngine.ProcessDeviceAlertsAsync(device, cancellationToken);

            // Send any generated alerts
            foreach (var alert in alerts)
            {
                try
                {
                    await alertEngine.SendDeviceAlertAsync(alert, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to send alert {AlertType} for device {DeviceId}",
                        alert.AlertType,
                        deviceId
                    );
                }
            }

            // Perform device-specific monitoring
            await PerformDeviceSpecificMonitoringAsync(device, cancellationToken);

            if (_options.EnableDebugLogging)
            {
                _logger.LogDebug(
                    "Completed device health processing for device {DeviceId}",
                    deviceId
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing device health for device {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// Perform device-specific monitoring based on device type
    /// </summary>
    /// <param name="device">Device to monitor</param>

    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task PerformDeviceSpecificMonitoringAsync(
        DeviceHealth device,

        CancellationToken cancellationToken
    )
    {
        try
        {
            switch (device.DeviceType)
            {
                case DeviceType.CGM:
                    await PerformCgmSpecificMonitoringAsync(device, cancellationToken);
                    break;
                case DeviceType.InsulinPump:
                    await PerformInsulinPumpSpecificMonitoringAsync(device, cancellationToken);
                    break;
                case DeviceType.BGM:
                    await PerformBgmSpecificMonitoringAsync(device, cancellationToken);
                    break;
                default:
                    // No device-specific monitoring for unknown devices
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error during device-specific monitoring for device {DeviceId}",
                device.DeviceId
            );
        }
    }

    /// <summary>
    /// Minimum data gap in minutes to consider for sensor warmup suggestion
    /// </summary>
    private const int MinimumGapMinutesForWarmupSuggestion = 60;

    /// <summary>
    /// Perform CGM-specific monitoring
    /// </summary>
    /// <param name="device">CGM device</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task PerformCgmSpecificMonitoringAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        // Monitor sensor session duration
        if (device.SensorExpiration.HasValue)
        {
            var hoursUntilExpiration = (device.SensorExpiration.Value - DateTime.UtcNow).TotalHours;

            if (hoursUntilExpiration <= 0)
            {
                _logger.LogWarning("CGM sensor for device {DeviceId} has expired", device.DeviceId);
            }
            else if (hoursUntilExpiration <= 24)
            {
                _logger.LogInformation(
                    "CGM sensor for device {DeviceId} expires in {Hours:F1} hours",
                    device.DeviceId,
                    hoursUntilExpiration
                );
            }
        }

        // Monitor calibration status
        if (device.LastCalibration.HasValue)
        {
            var hoursSinceCalibration = (DateTime.UtcNow - device.LastCalibration.Value).TotalHours;

            if (hoursSinceCalibration > device.CalibrationReminderHours * 2)
            {
                _logger.LogWarning(
                    "CGM device {DeviceId} calibration is overdue by {Hours:F1} hours",
                    device.DeviceId,
                    hoursSinceCalibration - device.CalibrationReminderHours
                );
            }
        }

        // Check data continuity and evaluate for sensor tracker suggestion
        await CheckDataContinuityAsync(device, cancellationToken);

        // Check if there's a significant data gap that might indicate sensor warmup
        if (device.LastDataReceived.HasValue)
        {
            var minutesSinceLastData = (DateTime.UtcNow - device.LastDataReceived.Value).TotalMinutes;

            if (minutesSinceLastData >= MinimumGapMinutesForWarmupSuggestion)
            {
                // Evaluate for sensor tracker suggestion (might be a warmup)
                await EvaluateSensorWarmupSuggestionAsync(device, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Evaluate if a sensor tracker suggestion should be created based on data gap
    /// </summary>
    /// <param name="device">The CGM device with a data gap</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task EvaluateSensorWarmupSuggestionAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var trackerSuggestionService = scope.ServiceProvider.GetRequiredService<ITrackerSuggestionService>();

            await trackerSuggestionService.EvaluateDataGapForTrackerSuggestionAsync(
                device.UserId,
                device.LastDataReceived!.Value,
                DateTime.UtcNow,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error evaluating sensor warmup suggestion for device {DeviceId}",
                device.DeviceId
            );
        }
    }

    /// <summary>
    /// Perform insulin pump-specific monitoring
    /// </summary>
    /// <param name="device">Insulin pump device</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task PerformInsulinPumpSpecificMonitoringAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        // This would monitor insulin reservoir levels, infusion set age, occlusion detection, etc.
        // For now, we'll perform basic monitoring

        await CheckDataContinuityAsync(device, cancellationToken);

        // Log pump-specific metrics if available
        _logger.LogDebug(
            "Performed insulin pump monitoring for device {DeviceId}",
            device.DeviceId
        );
    }

    /// <summary>
    /// Perform BGM-specific monitoring
    /// </summary>
    /// <param name="device">BGM device</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private async Task PerformBgmSpecificMonitoringAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        // This would monitor test strip inventory, control solution testing, etc.
        // For now, we'll perform basic monitoring

        await CheckDataContinuityAsync(device, cancellationToken);

        // Check calibration status for BGM devices
        if (device.LastCalibration.HasValue)
        {
            var daysSinceCalibration = (DateTime.UtcNow - device.LastCalibration.Value).TotalDays;

            if (daysSinceCalibration > 30)
            {
                _logger.LogWarning(
                    "BGM device {DeviceId} has not been calibrated for {Days:F0} days",
                    device.DeviceId,
                    daysSinceCalibration
                );
            }
        }
    }

    /// <summary>
    /// Check data continuity for a device
    /// </summary>
    /// <param name="device">Device to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    private Task CheckDataContinuityAsync(DeviceHealth device, CancellationToken cancellationToken)
    {
        if (!device.LastDataReceived.HasValue)
        {
            _logger.LogWarning("Device {DeviceId} has never received data", device.DeviceId);
            return Task.CompletedTask;
        }

        var minutesSinceLastData = (DateTime.UtcNow - device.LastDataReceived.Value).TotalMinutes;

        if (minutesSinceLastData > device.DataGapWarningMinutes)
        {
            var severity = minutesSinceLastData > 120 ? "critical" : "warning";
            _logger.LogWarning(
                "Device {DeviceId} has {Severity} data gap: {Minutes:F0} minutes since last data",
                device.DeviceId,
                severity,
                minutesSinceLastData
            );
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the background service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Device Health Monitoring Service is stopping");
        await base.StopAsync(cancellationToken);
    }
}

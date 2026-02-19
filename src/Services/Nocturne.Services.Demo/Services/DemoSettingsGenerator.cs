using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.Configuration;
using Nocturne.Services.Demo.Configuration;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Generates realistic demo UI settings configuration.
/// This provides all the settings data for the frontend settings pages in demo mode.
/// </summary>
public class DemoSettingsGenerator
{
    private readonly DemoModeConfiguration _config;

    public DemoSettingsGenerator(DemoModeConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Generates a complete UI settings configuration with realistic demo data.
    /// </summary>
    public UISettingsConfiguration GenerateSettings()
    {
        return new UISettingsConfiguration
        {
            Devices = GenerateDeviceSettings(),
            Algorithm = GenerateAlgorithmSettings(),
            Features = GenerateFeatureSettings(),
            Notifications = GenerateNotificationSettings(),
            Services = GenerateServicesSettings(),
        };
    }

    private DeviceSettings GenerateDeviceSettings()
    {
        return new DeviceSettings
        {
            ConnectedDevices = new List<ConnectedDevice>
            {
                new()
                {
                    Id = "demo-cgm-1",
                    Name = "Dexcom G7",
                    Type = "cgm",
                    Status = "connected",
                    Battery = 85,
                    LastSync = DateTimeOffset.UtcNow.AddMinutes(-5),
                    SerialNumber = "SM12345678",
                },
                new()
                {
                    Id = "demo-pump-1",
                    Name = "Omnipod 5",
                    Type = "pump",
                    Status = "connected",
                    Battery = 72,
                    LastSync = DateTimeOffset.UtcNow.AddMinutes(-2),
                    SerialNumber = "POD98765432",
                },
            },
            AutoConnect = true,
            ShowRawData = false,
            UploadEnabled = true,
            CgmConfiguration = new CgmConfiguration
            {
                DataSourcePriority = "cgm",
                SensorWarmupHours = 2,
            },
        };
    }

    private AlgorithmSettings GenerateAlgorithmSettings()
    {
        return new AlgorithmSettings
        {
            Prediction = new PredictionSettings
            {
                Enabled = true,
                Minutes = 30,
                Model = "ar2",
            },
            Autosens = new AutosensSettings
            {
                Enabled = true,
                Min = 0.7,
                Max = 1.2,
            },
            CarbAbsorption = new CarbAbsorptionSettings
            {
                DefaultMinutes = (int)_config.CarbAbsorptionDurationMinutes / 6, // Use carb absorption from config
                MinRateGramsPerHour = 4,
            },
            Loop = new LoopSettings
            {
                Enabled = false,
                Mode = "open",
                MaxBasalRate = _config.BasalRate * 4, // Standard 4x max temp basal
                MaxBolus = 10.0,
                SmbEnabled = false,
                UamEnabled = false,
            },
            SafetyLimits = new SafetyLimits { MaxIOB = 10.0, MaxDailyBasalMultiplier = 3.0 },
        };
    }

    private FeatureSettings GenerateFeatureSettings()
    {
        return new FeatureSettings
        {
            Display = new DisplaySettings
            {
                NightMode = false,
                Theme = "system",
                TimeFormat = "12",
                Units = "mg/dl",
                ShowRawBG = false,
                FocusHours = 3,
            },
            Widgets = new List<WidgetConfig>
            {
                // Top widgets
                new() { Id = WidgetId.BgDelta, Enabled = true, Placement = WidgetPlacement.Top },
                new() { Id = WidgetId.LastUpdated, Enabled = true, Placement = WidgetPlacement.Top },
                new() { Id = WidgetId.ConnectionStatus, Enabled = true, Placement = WidgetPlacement.Top },
                // Main sections
                new() { Id = WidgetId.GlucoseChart, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.Statistics, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.Predictions, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.DailyStats, Enabled = true, Placement = WidgetPlacement.Main },
                new() { Id = WidgetId.Treatments, Enabled = true, Placement = WidgetPlacement.Main },
            },
            Plugins = new Dictionary<string, PluginSettings>
            {
                {
                    "delta",
                    new PluginSettings { Enabled = true, Description = "Show glucose change" }
                },
                {
                    "direction",
                    new PluginSettings { Enabled = true, Description = "Trend arrow indicator" }
                },
                {
                    "timeago",
                    new PluginSettings { Enabled = true, Description = "Time since last reading" }
                },
                {
                    "iob",
                    new PluginSettings { Enabled = true, Description = "Insulin on board" }
                },
                {
                    "cob",
                    new PluginSettings { Enabled = true, Description = "Carbs on board" }
                },
                {
                    "basal",
                    new PluginSettings { Enabled = true, Description = "Current basal rate" }
                },
                {
                    "cage",
                    new PluginSettings { Enabled = false, Description = "Cannula/site age" }
                },
                {
                    "sage",
                    new PluginSettings { Enabled = true, Description = "Sensor age" }
                },
                {
                    "iage",
                    new PluginSettings { Enabled = false, Description = "Insulin reservoir age" }
                },
                {
                    "bage",
                    new PluginSettings { Enabled = false, Description = "Pump battery age" }
                },
                {
                    "pump",
                    new PluginSettings { Enabled = true, Description = "Pump status" }
                },
                {
                    "loop",
                    new PluginSettings { Enabled = true, Description = "Loop/OpenAPS status" }
                },
                {
                    "upbat",
                    new PluginSettings { Enabled = false, Description = "Uploader battery" }
                },
                {
                    "devicestatus",
                    new PluginSettings { Enabled = false, Description = "Device status details" }
                },
                {
                    "bwp",
                    new PluginSettings { Enabled = false, Description = "Bolus wizard preview" }
                },
                {
                    "treatmentnotify",
                    new PluginSettings { Enabled = true, Description = "Treatment notifications" }
                },
                {
                    "openaps",
                    new PluginSettings { Enabled = false, Description = "OpenAPS pill display" }
                },
            },
        };
    }

    private NotificationSettings GenerateNotificationSettings()
    {
        // Use target glucose from config to calculate alarm thresholds
        var targetGlucose = _config.TargetGlucose;
        var targetLow = (int)targetGlucose - 30;  // 80
        var targetHigh = (int)targetGlucose + 10; // 120
        var urgentLow = 55;
        var urgentHigh = 250;

        return new NotificationSettings
        {
            AlarmConfiguration = new UserAlarmConfiguration
            {
                Version = 1,
                Enabled = true,
                SoundEnabled = true,
                VibrationEnabled = true,
                GlobalVolume = 70,
                Profiles = new List<AlarmProfileConfiguration>
                {
                    new()
                    {
                        Id = "demo-urgent-low",
                        Name = "Urgent Low",
                        Description = "Critical low glucose alarm",
                        Enabled = true,
                        AlarmType = AlarmTriggerType.UrgentLow,
                        Threshold = urgentLow,
                        PersistenceMinutes = 0,
                        Priority = AlarmPriority.Critical,
                        OverrideQuietHours = true,
                        DisplayOrder = 0,
                        Audio = new AlarmAudioSettings
                        {
                            Enabled = true,
                            SoundId = "alarm-urgent",
                            AscendingVolume = true,
                            StartVolume = 50,
                            MaxVolume = 100,
                            AscendDurationSeconds = 30,
                        },
                        Visual = new AlarmVisualSettings
                        {
                            ScreenFlash = true,
                            PersistentBanner = true,
                            WakeScreen = true,
                        },
                    },
                    new()
                    {
                        Id = "demo-low",
                        Name = "Low",
                        Description = "Low glucose warning",
                        Enabled = true,
                        AlarmType = AlarmTriggerType.Low,
                        Threshold = targetLow - 10, // 70 mg/dL
                        PersistenceMinutes = 5,
                        Priority = AlarmPriority.High,
                        DisplayOrder = 1,
                        Audio = new AlarmAudioSettings
                        {
                            Enabled = true,
                            SoundId = "alarm-low",
                            AscendingVolume = false,
                            MaxVolume = 80,
                        },
                    },
                    new()
                    {
                        Id = "demo-high",
                        Name = "High",
                        Description = "High glucose warning",
                        Enabled = true,
                        AlarmType = AlarmTriggerType.High,
                        Threshold = targetHigh + 60, // 180 mg/dL
                        PersistenceMinutes = 15,
                        Priority = AlarmPriority.Normal,
                        DisplayOrder = 2,
                        Audio = new AlarmAudioSettings
                        {
                            Enabled = true,
                            SoundId = "alarm-high",
                            AscendingVolume = false,
                            MaxVolume = 70,
                        },
                    },
                    new()
                    {
                        Id = "demo-urgent-high",
                        Name = "Urgent High",
                        Description = "Critical high glucose alarm",
                        Enabled = true,
                        AlarmType = AlarmTriggerType.UrgentHigh,
                        Threshold = urgentHigh,
                        PersistenceMinutes = 10,
                        Priority = AlarmPriority.High,
                        DisplayOrder = 3,
                        Audio = new AlarmAudioSettings
                        {
                            Enabled = true,
                            SoundId = "alarm-urgent",
                            AscendingVolume = true,
                            StartVolume = 40,
                            MaxVolume = 100,
                            AscendDurationSeconds = 45,
                        },
                    },
                },
                QuietHours = new QuietHoursConfiguration
                {
                    Enabled = false,
                    StartTime = "22:00",
                    EndTime = "07:00",
                    AllowCritical = true,
                    ReduceVolume = true,
                    QuietVolume = 30,
                },
                Channels = new NotificationChannelsConfig
                {
                    Push = new ChannelConfig { Enabled = true },
                    Email = new ChannelConfig { Enabled = false },
                    Sms = new ChannelConfig { Enabled = false },
                },
                EmergencyContacts = new List<EmergencyContactConfig>
                {
                    new()
                    {
                        Id = "demo-contact-1",
                        Name = "Jane Doe",
                        Phone = "+1 555-0123",
                        CriticalOnly = true,
                        DelayMinutes = 5,
                        Enabled = true,
                    },
                },
            },
        };
    }


    private ServicesSettings GenerateServicesSettings()
    {
        return new ServicesSettings
        {
            ConnectedServices = new List<ConnectedService>
            {
                new()
                {
                    Id = "dexcom-share-1",
                    Name = "Dexcom Share",
                    Type = "cgm",
                    Description = "Dexcom G7 - Share account",
                    Status = "connected",
                    LastSync = DateTimeOffset.UtcNow.AddMinutes(-2),
                    Icon = "dexcom",
                    Configured = true,
                    Enabled = true,
                },
                new()
                {
                    Id = "nightscout-backup-1",
                    Name = "Nightscout Backup",
                    Type = "data",
                    Description = "yoursite.herokuapp.com",
                    Status = "connected",
                    LastSync = DateTimeOffset.UtcNow.AddMinutes(-15),
                    Icon = "nightscout",
                    Configured = true,
                    Enabled = true,
                },
            },
            AvailableServices = ConnectorMetadataService.GetAvailableServices(),
            SyncSettings = new SyncSettings
            {
                AutoSync = true,
                SyncOnAppOpen = true,
                BackgroundRefresh = true,
            },
        };
    }
}

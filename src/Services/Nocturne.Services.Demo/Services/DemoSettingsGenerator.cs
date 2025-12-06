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
            DashboardWidgets = new DashboardWidgets
            {
                GlucoseChart = true,
                Statistics = true,
                Treatments = true,
                Predictions = true,
                Agp = false,
                DailyStats = true,
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
            AlarmsEnabled = true,
            SoundEnabled = true,
            VibrationEnabled = true,
            Volume = 70,
            Alarms = new AlarmSettings
            {
                UrgentHigh = new AlarmConfig
                {
                    Enabled = true,
                    Threshold = urgentHigh,
                    Sound = "alarm-urgent",
                    RepeatMinutes = 5,
                    SnoozeOptions = new List<int> { 5, 10, 15, 30 },
                },
                High = new AlarmConfig
                {
                    Enabled = true,
                    Threshold = targetHigh + 60, // 180 mg/dL
                    Sound = "alarm-high",
                    RepeatMinutes = 15,
                    SnoozeOptions = new List<int> { 15, 30, 60 },
                },
                Low = new AlarmConfig
                {
                    Enabled = true,
                    Threshold = targetLow - 10, // 70 mg/dL
                    Sound = "alarm-low",
                    RepeatMinutes = 5,
                    SnoozeOptions = new List<int> { 10, 15, 30 },
                },
                UrgentLow = new AlarmConfig
                {
                    Enabled = true,
                    Threshold = urgentLow,
                    Sound = "alarm-urgent",
                    RepeatMinutes = 5,
                    SnoozeOptions = new List<int> { 5, 10, 15 },
                },
                StaleData = new StaleDataAlarm
                {
                    Enabled = true,
                    WarningMinutes = 15,
                    UrgentMinutes = 30,
                    Sound = "alert",
                },
            },
            QuietHours = new QuietHoursSettings
            {
                Enabled = false,
                StartTime = "22:00",
                EndTime = "07:00",
            },
            Channels = new NotificationChannels
            {
                Push = new NotificationChannel { Enabled = true, Label = "Push Notifications" },
                Email = new NotificationChannel { Enabled = false, Label = "Email" },
                Sms = new NotificationChannel { Enabled = false, Label = "SMS" },
            },
            EmergencyContacts = new List<EmergencyContact>
            {
                new()
                {
                    Id = "demo-contact-1",
                    Name = "Jane Doe",
                    Phone = "+1 555-0123",
                    NotifyOnUrgent = true,
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
            AvailableServices = new List<AvailableService>
            {
                new()
                {
                    Id = "dexcom",
                    Name = "Dexcom",
                    Type = "cgm",
                    Description = "Connect to Dexcom Share or Clarity",
                    Icon = "dexcom",
                },
                new()
                {
                    Id = "freestyle",
                    Name = "FreeStyle Libre",
                    Type = "cgm",
                    Description = "Connect to LibreView for CGM data",
                    Icon = "libre",
                },
                new()
                {
                    Id = "medtronic",
                    Name = "Medtronic CareLink",
                    Type = "pump",
                    Description = "Sync data from MiniMed pumps",
                    Icon = "medtronic",
                },
                new()
                {
                    Id = "nightscout",
                    Name = "Nightscout",
                    Type = "data",
                    Description = "Sync with an existing Nightscout instance",
                    Icon = "nightscout",
                },
                new()
                {
                    Id = "glooko",
                    Name = "Glooko",
                    Type = "data",
                    Description = "Import data from Glooko platform",
                    Icon = "glooko",
                },
                new()
                {
                    Id = "myfitnesspal",
                    Name = "MyFitnessPal",
                    Type = "food",
                    Description = "Import meals and nutrition data",
                    Icon = "myfitnesspal",
                },
            },
            SyncSettings = new SyncSettings
            {
                AutoSync = true,
                SyncOnAppOpen = true,
                BackgroundRefresh = true,
            },
        };
    }
}

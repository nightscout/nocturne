using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Factory for creating test data with dynamic IDs and consistent timestamps
/// Eliminates hard-coded ObjectIds and time-dependent test data
/// </summary>
public static class TestDataFactory
{
    /// <summary>
    /// Creates a test Entry with optional customization
    /// </summary>
    /// <param name="id">Custom ID (generates unique if null)</param>
    /// <param name="timestamp">Custom timestamp (uses TestTimeProvider if null)</param>
    /// <param name="deviceSuffix">Custom device suffix for uniqueness</param>
    /// <param name="sgv">Blood glucose value</param>
    /// <param name="type">Entry type (sgv, mbg, cal)</param>
    /// <returns>Entry with dynamic data</returns>
    public static Entry CreateEntry(
        string? id = null,
        DateTimeOffset? timestamp = null,
        string? deviceSuffix = null,
        int? sgv = null,
        string type = "sgv"
    )
    {
        var testTime = timestamp ?? TestTimeProvider.GetTestTime();
        var uniqueSuffix = deviceSuffix ?? Guid.NewGuid().ToString()[..8];

        return new Entry
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Mills = testTime.ToUnixTimeMilliseconds(),
            DateString = testTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Sgv = sgv ?? 120,
            Direction = "Flat",
            Type = type,
            Device = $"test-device-{uniqueSuffix}",
            Delta = 0,
            Rssi = 100,
            Noise = 1,
        };
    }

    /// <summary>
    /// Creates a test Treatment with optional customization
    /// </summary>
    /// <param name="id">Custom ID (generates unique if null)</param>
    /// <param name="timestamp">Custom timestamp (uses TestTimeProvider if null)</param>
    /// <param name="eventType">Treatment event type</param>
    /// <param name="insulin">Insulin amount</param>
    /// <returns>Treatment with dynamic data</returns>
    public static Treatment CreateTreatment(
        string? id = null,
        DateTimeOffset? timestamp = null,
        string eventType = "Correction Bolus",
        double? insulin = null
    )
    {
        var testTime = timestamp ?? TestTimeProvider.GetTestTime();

        return new Treatment
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Mills = testTime.ToUnixTimeMilliseconds(),
            CreatedAt = testTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            EventType = eventType,
            Insulin = insulin ?? 1.5,
            Notes = $"Test treatment {Guid.NewGuid().ToString()[..8]}",
        };
    }

    /// <summary>
    /// Creates a test Food with optional customization
    /// </summary>
    /// <param name="id">Custom ID (generates unique if null)</param>
    /// <param name="name">Food name</param>
    /// <param name="category">Food category</param>
    /// <returns>Food with dynamic data</returns>
    public static Food CreateFood(string? id = null, string? name = null, string category = "Dairy")
    {
        var uniqueSuffix = Guid.NewGuid().ToString()[..8];

        return new Food
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = "food",
            Category = category,
            Name = name ?? $"Test Food {uniqueSuffix}",
            Portion = 100,
            Unit = "ml",
            Carbs = 12,
            Protein = 3,
            Fat = 2,
            Energy = 284, // Energy in kilojoules (68 calories * 4.184)
        };
    }

    /// <summary>
    /// Creates a test Profile with optional customization
    /// </summary>
    /// <param name="id">Custom ID (generates unique if null)</param>
    /// <param name="defaultProfile">Profile name</param>
    /// <param name="timestamp">Start date (uses TestTimeProvider if null)</param>
    /// <returns>Profile with dynamic data</returns>
    public static Profile CreateProfile(
        string? id = null, string? defaultProfile = null, DateTimeOffset? timestamp = null)
    {
        var uniqueSuffix = Guid.NewGuid().ToString()[..8];
        var profileName = defaultProfile ?? $"test-profile-{uniqueSuffix}";
        var startDate = timestamp ?? TestTimeProvider.GetTestTime();

        return new Profile
        {
            Id = id ?? Guid.NewGuid().ToString(),
            DefaultProfile = profileName,
            Mills = startDate.ToUnixTimeMilliseconds(),
            StartDate = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Units = "mg/dL",
            Store = new Dictionary<string, ProfileData>
            {
                [profileName] = new ProfileData
                {
                    Dia = 3.0,
                    CarbsHr = 20,
                    Delay = 20,
                    Timezone = "UTC",
                    Units = "mg/dL",
                    Basal = new List<TimeValue>
                    {
                        new TimeValue { Time = "00:00", Value = 0.5 },
                        new TimeValue { Time = "06:00", Value = 0.7 },
                        new TimeValue { Time = "09:00", Value = 0.8 },
                        new TimeValue { Time = "21:00", Value = 0.6 },
                    },
                    CarbRatio = new List<TimeValue>
                    {
                        new TimeValue { Time = "00:00", Value = 15.0 },
                        new TimeValue { Time = "07:00", Value = 12.0 },
                        new TimeValue { Time = "12:00", Value = 15.0 },
                        new TimeValue { Time = "18:00", Value = 18.0 },
                    },
                    Sens = new List<TimeValue>
                    {
                        new TimeValue { Time = "00:00", Value = 100.0 },
                        new TimeValue { Time = "06:00", Value = 95.0 },
                        new TimeValue { Time = "09:00", Value = 100.0 },
                        new TimeValue { Time = "17:00", Value = 110.0 },
                    },
                    TargetLow = new List<TimeValue>
                    {
                        new TimeValue { Time = "00:00", Value = 100.0 },
                    },
                    TargetHigh = new List<TimeValue>
                    {
                        new TimeValue { Time = "00:00", Value = 180.0 },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Creates a test DeviceStatus with optional customization
    /// </summary>
    /// <param name="id">Custom ID (generates unique if null)</param>
    /// <param name="timestamp">Custom timestamp (uses TestTimeProvider if null)</param>
    /// <param name="device">Device name</param>
    /// <returns>DeviceStatus with dynamic data</returns>
    public static DeviceStatus CreateDeviceStatus(
        string? id = null,
        DateTimeOffset? timestamp = null,
        string? device = null
    )
    {
        var testTime = timestamp ?? TestTimeProvider.GetTestTime();
        var uniqueSuffix = Guid.NewGuid().ToString()[..8];

        return new DeviceStatus
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Mills = testTime.ToUnixTimeMilliseconds(),
            CreatedAt = testTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Device = device ?? $"test-device-{uniqueSuffix}",
            Uploader = new UploaderStatus { Battery = 80 },
        };
    }

    /// <summary>
    /// Creates a collection of test entries with sequential timestamps
    /// </summary>
    /// <param name="count">Number of entries to create</param>
    /// <param name="intervalMinutes">Minutes between each entry</param>
    /// <param name="startTime">Starting timestamp (uses TestTimeProvider if null)</param>
    /// <returns>Array of entries with sequential timestamps</returns>
    public static Entry[] CreateEntrySequence(
        int count = 3,
        int intervalMinutes = 5,
        DateTimeOffset? startTime = null
    )
    {
        var baseTime = startTime ?? TestTimeProvider.GetTestTime();
        var entries = new Entry[count];

        for (int i = 0; i < count; i++)
        {
            var timestamp = baseTime.AddMinutes(-i * intervalMinutes);
            entries[i] = CreateEntry(
                timestamp: timestamp,
                sgv: 120 - (i * 2), // Slightly decreasing values
                deviceSuffix: $"seq-{i}"
            );
        }

        return entries;
    }

    /// <summary>
    /// Creates test data for specific entry types (sgv, mbg, cal)
    /// </summary>
    /// <param name="includeBloodGlucose">Include SGV entries</param>
    /// <param name="includeMeterBg">Include meter BG entries</param>
    /// <param name="includeCalibration">Include calibration entries</param>
    /// <returns>Array of mixed entry types</returns>
    public static Entry[] CreateMixedEntryTypes(
        bool includeBloodGlucose = true,
        bool includeMeterBg = true,
        bool includeCalibration = true
    )
    {
        var entries = new List<Entry>();
        var baseTime = TestTimeProvider.GetTestTime();

        if (includeBloodGlucose)
        {
            entries.Add(CreateEntry(timestamp: baseTime, type: "sgv", sgv: 120));
        }

        // A meter reading carries mbg and a calibration slope/intercept/scale, with none of a
        // sensor reading's sgv, direction, noise, delta or rssi, as uploaders send them.
        if (includeMeterBg)
        {
            var meter = CreateEntry(timestamp: baseTime.AddMinutes(-5), type: "mbg");
            meter.Mbg = 115;
            entries.Add(WithoutSensorFields(meter));
        }

        if (includeCalibration)
        {
            var calibration = CreateEntry(timestamp: baseTime.AddMinutes(-10), type: "cal");
            calibration.Slope = 850;
            calibration.Intercept = 32000;
            calibration.Scale = 1;
            entries.Add(WithoutSensorFields(calibration));
        }

        return entries.ToArray();
    }

    private static Entry WithoutSensorFields(Entry entry)
    {
        entry.Sgv = null;
        entry.Direction = null;
        entry.Noise = null;
        entry.Delta = null;
        entry.Rssi = null;
        return entry;
    }
}

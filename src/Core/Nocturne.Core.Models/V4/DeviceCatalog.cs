namespace Nocturne.Core.Models.V4;

/// <summary>
/// Static catalog of known CGM and insulin pump device models.
/// </summary>
public static class DeviceCatalog
{
    private static readonly DeviceEventType[] PodResets =
        [DeviceEventType.PodChange, DeviceEventType.PodActivated, DeviceEventType.SiteChange];
    private static readonly DeviceEventType[] SiteResets = [DeviceEventType.SiteChange, DeviceEventType.CannulaChange];
    private static readonly DeviceEventType[] ReservoirResets = [DeviceEventType.InsulinChange, DeviceEventType.ReservoirChange];

    /// <summary>A pod, patch or cartridge: the site, the insulin and a sealed battery in one disposable.</summary>
    private static PumpComponent Pod(int graceHours) => new()
    {
        Key = "pod",
        Kind = PumpComponentKind.Pod,
        Provides = [PumpCapability.Site, PumpCapability.Reservoir, PumpCapability.Power],
        Lifecycle = ComponentLifecycle.Disposable,
        LifespanHours = 72,
        GraceHours = graceHours,
        Power = ComponentPower.Embedded,
        ResetBy = PodResets,
        EndedBy = [DeviceEventType.PodDeactivated],
    };

    private static PumpComponent InfusionSet(int hours = 72) => new()
    {
        Key = "infusion-set",
        Kind = PumpComponentKind.InfusionSet,
        Provides = [PumpCapability.Site],
        Lifecycle = ComponentLifecycle.Disposable,
        LifespanHours = hours,
        ResetBy = SiteResets,
    };

    private static PumpComponent Reservoir(int hours = 72, ComponentPower? power = null) => new()
    {
        Key = "reservoir",
        Kind = PumpComponentKind.Reservoir,
        Provides = power is null ? [PumpCapability.Reservoir] : [PumpCapability.Reservoir, PumpCapability.Power],
        Lifecycle = ComponentLifecycle.Disposable,
        LifespanHours = hours,
        Power = power,
        ResetBy = ReservoirResets,
    };

    /// <summary>Insulet labels a pod for 72 hours, and it keeps delivering for 8 more before it stops.</summary>
    private static readonly PumpProperties Omnipod = new() { Components = [Pod(graceHours: 8)] };

    /// <summary>A patch runs 72 hours and stops at 3 days 8 hours; the base is reused and has no rated life.</summary>
    private static readonly PumpProperties Medtrum = new()
    {
        Components =
        [
            Pod(graceHours: 8),
            new()
            {
                Key = "base", Kind = PumpComponentKind.Base, Provides = [], Lifecycle = ComponentLifecycle.Durable,
            },
        ],
    };

    /// <summary>
    /// The cartridge carries the cannula, the insulin and the battery for 72 hours, with 12 more before
    /// it stops. The controller is powered by the cartridge, never charged, and lasts about two years.
    /// </summary>
    private static readonly PumpProperties Mint = new()
    {
        Components =
        [
            Pod(graceHours: 12),
            new()
            {
                Key = "controller", Kind = PumpComponentKind.Controller, Provides = [],
                Lifecycle = ComponentLifecycle.Durable, LifespanHours = 2 * 365 * 24, TrackedByDefault = false,
            },
        ],
    };

    /// <summary>
    /// Not a pod: the reservoir and the infusion set are changed apart, on the pump body that holds the
    /// rechargeable battery.
    /// </summary>
    private static readonly PumpProperties Equil = new()
    {
        Components =
        [
            InfusionSet(),
            Reservoir(),
            new()
            {
                Key = "pump", Kind = PumpComponentKind.Base, Provides = [PumpCapability.Power],
                Lifecycle = ComponentLifecycle.Durable, Power = ComponentPower.Rechargeable,
            },
        ],
    };

    /// <summary>
    /// The 4-day reservoir holds a sealed zinc-air battery. Roche's interval for the pump base varies
    /// by region from 90 to 180 days, so its tracker is off unless asked for, at the shortest.
    /// </summary>
    private static readonly PumpProperties AccuChekSolo = new()
    {
        Components =
        [
            InfusionSet(),
            Reservoir(hours: 96, power: ComponentPower.Embedded),
            new()
            {
                Key = "base", Kind = PumpComponentKind.Base, Provides = [], Lifecycle = ComponentLifecycle.Durable,
                LifespanHours = 90 * 24, TrackedByDefault = false,
            },
        ],
    };

    private static readonly PumpProperties Tubed = new() { Components = [InfusionSet(), Reservoir()] };

    private static readonly IReadOnlyList<DeviceCatalogEntry> _entries =
    [
        // CGMs — Dexcom
        new() { Id = "dexcom-g7",        Name = "Dexcom G7",          Manufacturer = "Dexcom", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 10, GraceHours = 12, WarmupMinutes = 30,  UpdateIntervalMinutes = 5, HasSeparateTransmitter = false } },
        new() { Id = "dexcom-g7-15-day", Name = "Dexcom G7 15 Day",   Manufacturer = "Dexcom", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 15, GraceHours = 12, WarmupMinutes = 60,  UpdateIntervalMinutes = 5, HasSeparateTransmitter = false } },
        new() { Id = "dexcom-g6",        Name = "Dexcom G6",          Manufacturer = "Dexcom", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 10, WarmupMinutes = 120, UpdateIntervalMinutes = 5, HasSeparateTransmitter = true, TransmitterDurationDays = 90 } },
        new() { Id = "dexcom-one-plus",  Name = "Dexcom ONE+",        Manufacturer = "Dexcom", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 10, GraceHours = 12, WarmupMinutes = 30, UpdateIntervalMinutes = 5, HasSeparateTransmitter = false } },
        new() { Id = "dexcom-one",       Name = "Dexcom ONE",         Manufacturer = "Dexcom", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 10, WarmupMinutes = 120, UpdateIntervalMinutes = 5, HasSeparateTransmitter = true, TransmitterDurationDays = 90 } },

        // CGMs — Abbott
        new() { Id = "libre-3",          Name = "FreeStyle Libre 3",  Manufacturer = "Abbott", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 14, WarmupMinutes = 60, UpdateIntervalMinutes = 1, HasSeparateTransmitter = false } },
        new() { Id = "libre-3-plus",     Name = "FreeStyle Libre 3+", Manufacturer = "Abbott", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 15, WarmupMinutes = 60, UpdateIntervalMinutes = 1, HasSeparateTransmitter = false } },
        new() { Id = "libre-2-plus",     Name = "FreeStyle Libre 2+", Manufacturer = "Abbott", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 15, WarmupMinutes = 60, UpdateIntervalMinutes = 1, HasSeparateTransmitter = false } },
        new() { Id = "libre-2",          Name = "FreeStyle Libre 2",  Manufacturer = "Abbott", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 14, WarmupMinutes = 60, UpdateIntervalMinutes = 1, HasSeparateTransmitter = false } },

        // CGMs — Medtronic
        new() { Id = "medtronic-guardian-4", Name = "Guardian 4", Manufacturer = "Medtronic", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 7, WarmupMinutes = 120, UpdateIntervalMinutes = 5, HasSeparateTransmitter = false } },
        new() { Id = "medtronic-guardian-3", Name = "Guardian 3", Manufacturer = "Medtronic", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 7, WarmupMinutes = 120, UpdateIntervalMinutes = 5, HasSeparateTransmitter = true, TransmitterDurationDays = 365 } },

        // CGMs — Others
        new() { Id = "libre-1",              Name = "FreeStyle Libre",       Manufacturer = "Abbott",    Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 14, WarmupMinutes = 60,   UpdateIntervalMinutes = 5,  HasSeparateTransmitter = false } },
        new() { Id = "medtrum-nano-cgm",     Name = "TouchCare Nano CGM",    Manufacturer = "Medtrum",   Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 14, WarmupMinutes = 120,  UpdateIntervalMinutes = 2, HasSeparateTransmitter = true, TransmitterDurationDays = 365 } },
        new() { Id = "sibionics-gs1",        Name = "Sibionics GS1",         Manufacturer = "Sibionics", Category = DeviceCategory.CGM, Cgm = new() { SensorDurationDays = 14, WarmupMinutes = 60,   UpdateIntervalMinutes = 5, HasSeparateTransmitter = false } },

        // CGMs — Custom
        new() { Id = "custom-cgm", Name = "Custom CGM", Manufacturer = "Custom", Category = DeviceCategory.CGM },

        // Pumps — worn on the body (IsPatch only where one disposable is the site and the reservoir)
        new() { Id = "omnipod-5",           Name = "Omnipod 5",             Manufacturer = "Insulet",   Category = DeviceCategory.InsulinPump, Pump = Omnipod },
        new() { Id = "omnipod-dash",        Name = "Omnipod DASH",          Manufacturer = "Insulet",   Category = DeviceCategory.InsulinPump, Pump = Omnipod },
        new() { Id = "omnipod-eros",        Name = "Omnipod Eros",          Manufacturer = "Insulet",   Category = DeviceCategory.InsulinPump, Pump = Omnipod },
        new() { Id = "medtrum-nano",        Name = "TouchCare Nano",        Manufacturer = "Medtrum",   Category = DeviceCategory.InsulinPump, Pump = Medtrum },
        new() { Id = "medtrum-nano-300u",   Name = "TouchCare Nano 300U",   Manufacturer = "Medtrum",   Category = DeviceCategory.InsulinPump, Pump = Medtrum },
        new() { Id = "beta-bionics-mint",   Name = "Mint",                  Manufacturer = "Beta Bionics", Category = DeviceCategory.InsulinPump, Pump = Mint },
        new() { Id = "equil",               Name = "Equil",                 Manufacturer = "MicroTech", Category = DeviceCategory.InsulinPump, Pump = Equil },
        new() { Id = "accu-chek-solo",      Name = "Accu-Chek Solo",        Manufacturer = "Roche",     Category = DeviceCategory.InsulinPump, Pump = AccuChekSolo },

        // Pumps — tubed
        new() { Id = "tandem-tslim-x2",     Name = "t:slim X2",             Manufacturer = "Tandem",    Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "tandem-mobi",         Name = "t:slim Mobi",           Manufacturer = "Tandem",    Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "medtronic-780g",      Name = "MiniMed 780G",          Manufacturer = "Medtronic", Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "medtronic-770g",      Name = "MiniMed 770G",          Manufacturer = "Medtronic", Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "medtronic-paradigm",  Name = "MiniMed Paradigm / Veo", Manufacturer = "Medtronic", Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "ypsopump",            Name = "YpsoPump",              Manufacturer = "Ypsomed",   Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "dana-i",              Name = "Dana-i",                Manufacturer = "SOOIL",     Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "dana-rs",             Name = "Dana RS",               Manufacturer = "SOOIL",     Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "dana-r",              Name = "Dana R",                Manufacturer = "SOOIL",     Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "accu-chek-combo",     Name = "Accu-Chek Combo",       Manufacturer = "Roche",     Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "accu-chek-insight",   Name = "Accu-Chek Insight",     Manufacturer = "Roche",     Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "diaconn-g8",          Name = "Diaconn G8",            Manufacturer = "G2e",       Category = DeviceCategory.InsulinPump, Pump = Tubed },
        new() { Id = "custom-pump",         Name = "Custom Pump",           Manufacturer = "Custom",    Category = DeviceCategory.InsulinPump },
    ];

    /// <summary>
    /// Returns all known device catalog entries across all categories.
    /// </summary>
    /// <returns>A read-only list of all <see cref="DeviceCatalogEntry"/> records.</returns>
    public static IReadOnlyList<DeviceCatalogEntry> GetAll() => _entries;

    /// <summary>
    /// Looks up a device catalog entry by its unique identifier.
    /// </summary>
    /// <param name="id">The catalog identifier (e.g., "omnipod-5", "dexcom-g7").</param>
    /// <returns>The matching <see cref="DeviceCatalogEntry"/>, or <c>null</c> if not found.</returns>
    public static DeviceCatalogEntry? GetById(string id) =>
        _entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Returns all catalog entries that belong to the specified <see cref="DeviceCategory"/>.
    /// </summary>
    /// <param name="category">The device category to filter by.</param>
    /// <returns>A read-only list of <see cref="DeviceCatalogEntry"/> records in the given category.</returns>
    public static IReadOnlyList<DeviceCatalogEntry> GetByCategory(DeviceCategory category) =>
        _entries.Where(e => e.Category == category).ToList();
}

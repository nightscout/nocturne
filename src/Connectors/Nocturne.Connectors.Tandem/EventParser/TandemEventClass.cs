namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// Groups decoded pump events into the processing classes used by the connector's mappers.
/// Ported from <c>tconnectsync</c>'s <c>domain/tandemsource/event_class.py</c>, keyed by the raw
/// LID event name.
/// </summary>
public enum TandemEventClass
{
    Basal,
    BasalSuspension,
    BasalResume,
    Alarm,
    Bolus,
    Cartridge,
    CgmAlert,
    CgmStartJoinStop,
    CgmReading,
    UserMode,
    DeviceStatus,
}

public static class TandemEventClasses
{
    public static readonly IReadOnlySet<string> CgmSessionStart =
        new HashSet<string> { "LID_CGM_START_SESSION_GX", "LID_CGM_START_SESSION_FSL2" };

    public static readonly IReadOnlySet<string> CgmSessionJoin = new HashSet<string>
    {
        "LID_CGM_JOIN_SESSION_GX", "LID_CGM_JOIN_SESSION_G7",
        "LID_CGM_JOIN_SESSION_FSL2", "LID_CGM_JOIN_SESSION_FSL3",
    };

    public static readonly IReadOnlySet<string> CgmSessionStop = new HashSet<string>
    {
        "LID_CGM_STOP_SESSION_GX", "LID_CGM_STOP_SESSION_G7",
        "LID_CGM_STOP_SESSION_FSL2", "LID_CGM_STOP_SESSION_FSL3",
    };

    private static readonly IReadOnlyDictionary<string, TandemEventClass> ByName = BuildLookup();

    /// <summary>Returns the processing class for a decoded event, or null if it is not handled.</summary>
    public static TandemEventClass? ForEvent(TandemPumpEvent ev) =>
        ByName.TryGetValue(ev.Name, out var clazz) ? clazz : null;

    private static IReadOnlyDictionary<string, TandemEventClass> BuildLookup()
    {
        var map = new Dictionary<string, TandemEventClass>
        {
            ["LID_BASAL_DELIVERY"] = TandemEventClass.Basal,
            ["LID_PUMPING_SUSPENDED"] = TandemEventClass.BasalSuspension,
            ["LID_PUMPING_RESUMED"] = TandemEventClass.BasalResume,
            ["LID_ALARM_ACTIVATED"] = TandemEventClass.Alarm,
            ["LID_MALFUNCTION_ACTIVATED"] = TandemEventClass.Alarm,
            ["LID_BOLUS_REQUESTED_MSG1"] = TandemEventClass.Bolus,
            ["LID_BOLUS_REQUESTED_MSG2"] = TandemEventClass.Bolus,
            ["LID_BOLUS_REQUESTED_MSG3"] = TandemEventClass.Bolus,
            ["LID_BOLUS_COMPLETED"] = TandemEventClass.Bolus,
            ["LID_BOLEX_COMPLETED"] = TandemEventClass.Bolus,
            ["LID_CARTRIDGE_FILLED"] = TandemEventClass.Cartridge,
            ["LID_CANNULA_FILLED"] = TandemEventClass.Cartridge,
            ["LID_TUBING_FILLED"] = TandemEventClass.Cartridge,
            ["LID_CGM_ALERT_ACTIVATED"] = TandemEventClass.CgmAlert,
            ["LID_CGM_ALERT_ACTIVATED_DEX"] = TandemEventClass.CgmAlert,
            ["LID_CGM_ALERT_ACTIVATED_FSL2"] = TandemEventClass.CgmAlert,
            ["LID_CGM_DATA_GXB"] = TandemEventClass.CgmReading,
            ["LID_CGM_DATA_G7"] = TandemEventClass.CgmReading,
            ["LID_CGM_DATA_FSL2"] = TandemEventClass.CgmReading,
            ["LID_CGM_DATA_FSL3"] = TandemEventClass.CgmReading,
            ["LID_AA_USER_MODE_CHANGE"] = TandemEventClass.UserMode,
            ["LID_DAILY_BASAL"] = TandemEventClass.DeviceStatus,
        };

        foreach (var name in CgmSessionStart.Concat(CgmSessionJoin).Concat(CgmSessionStop))
            map[name] = TandemEventClass.CgmStartJoinStop;

        return map;
    }
}

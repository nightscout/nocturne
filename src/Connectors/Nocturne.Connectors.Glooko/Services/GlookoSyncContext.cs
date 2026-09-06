using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     One sync run's working state: the tenant's configuration, the Glooko session that run
///     authenticated with, and the mappers bound to that configuration's time mapping. Created in
///     the sync entry point and threaded through every helper, so a connector instance reached by
///     two overlapping runs cannot serve one tenant's session, patient code or timezone timeline
///     to another tenant's requests.
/// </summary>
internal sealed class GlookoSyncContext
{
    internal GlookoSyncContext(GlookoConnectorConfiguration config, string connectorSource, ILogger logger)
    {
        Config = config;
        TimeMapper = new GlookoTimeMapper(config, logger);
        SensorGlucoseMapper = new GlookoSensorGlucoseMapper(config, connectorSource, TimeMapper, logger);
        V4TreatmentMapper = new GlookoV4TreatmentMapper(connectorSource, TimeMapper, logger);
        StateSpanMapper = new GlookoStateSpanMapper(connectorSource, TimeMapper, logger);
        TempBasalMapper = new GlookoTempBasalMapper(connectorSource, TimeMapper, logger);
        SystemEventMapper = new GlookoSystemEventMapper(connectorSource, TimeMapper, logger);
        ProfileMapper = new GlookoProfileMapper(connectorSource, logger);
        PumpEventMapper = new GlookoPumpEventMapper(connectorSource, TimeMapper, logger);
        DeviceMapper = new GlookoDeviceMapper(connectorSource, TimeMapper, logger);
        NoteMapper = new GlookoNoteMapper(connectorSource, TimeMapper, logger);
        ActivityMapper = new GlookoActivityMapper(connectorSource, TimeMapper, logger);
        BodyWeightMapper = new GlookoBodyWeightMapper(connectorSource, TimeMapper, logger);
        StepCountMapper = new GlookoStepCountMapper(connectorSource, TimeMapper, logger);
        HeartRateMapper = new GlookoHeartRateMapper(connectorSource, TimeMapper, logger);
        SettingsProfileMapper = new GlookoSettingsProfileMapper(connectorSource, logger);
    }

    internal GlookoConnectorConfiguration Config { get; }

    internal GlookoTimeMapper TimeMapper { get; }
    internal GlookoSensorGlucoseMapper SensorGlucoseMapper { get; }
    internal GlookoV4TreatmentMapper V4TreatmentMapper { get; }
    internal GlookoStateSpanMapper StateSpanMapper { get; }
    internal GlookoTempBasalMapper TempBasalMapper { get; }
    internal GlookoSystemEventMapper SystemEventMapper { get; }
    internal GlookoProfileMapper ProfileMapper { get; }

    // SSV2-only mappers. They are built here rather than in the SSV2 path so that, like every
    // mapper above, they are bound to this run's time mapping and cannot be shared across tenants.
    internal GlookoPumpEventMapper PumpEventMapper { get; }
    internal GlookoDeviceMapper DeviceMapper { get; }
    internal GlookoNoteMapper NoteMapper { get; }
    internal GlookoActivityMapper ActivityMapper { get; }
    internal GlookoBodyWeightMapper BodyWeightMapper { get; }
    internal GlookoStepCountMapper StepCountMapper { get; }
    internal GlookoHeartRateMapper HeartRateMapper { get; }
    internal GlookoSettingsProfileMapper SettingsProfileMapper { get; }

    /// <summary>Glooko's session cookie, which doubles as this run's auth token. Null until authenticated.</summary>
    internal string? SessionCookie { get; set; }

    internal GlookoUserData? UserData { get; set; }

    /// <summary>The patient code every patient-scoped Glooko URL is built from.</summary>
    internal string? PatientCode => UserData?.GlookoCode;

    /// <summary>Meter units from the V3 profile; fetched once per run.</summary>
    internal string? MeterUnits { get; set; }

    /// <summary>The account's home timezone from the V3 profile; seeds the timeline origin.</summary>
    internal string? Timezone { get; set; }

    /// <summary>
    ///     Drops everything Glooko told this run about the account — the session and the profile
    ///     values resolved through it — so a 403 recovery re-authenticates and re-resolves from
    ///     scratch rather than reusing a patient code or home zone tied to the stale session.
    /// </summary>
    internal void ClearSessionAndProfile()
    {
        SessionCookie = null;
        UserData = null;
        MeterUnits = null;
        Timezone = null;
    }
}

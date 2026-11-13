using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Which resume watermark a route's broadcast origin is read from. <see cref="Live"/> is for
///     routes that never resolve one: a category with no broadcast yet, or a publisher member that
///     takes no origin.
/// </summary>
internal enum PublishOriginFamily
{
    Glucose,
    Treatment,
    Device,
    Live,
}

/// <summary>
///     How one record type reaches the publisher. <see cref="DataTypes"/> lists the sync data types
///     the record type may be gated and counted under; the first is the default. An empty list
///     means the record type is published only through the batch and direct entry points.
/// </summary>
internal abstract class PublishRoute
{
    protected PublishRoute(Type recordType, SyncDataType[] dataTypes, PublishOriginFamily origin)
    {
        RecordType = recordType;
        DataTypes = dataTypes;
        Origin = origin;
    }

    public Type RecordType { get; }

    public IReadOnlyList<SyncDataType> DataTypes { get; }

    public SyncDataType DefaultDataType =>
        DataTypes.Count > 0
            ? DataTypes[0]
            : throw new InvalidOperationException(
                $"{RecordType.Name} has no sync data type, so it publishes only by route, not counted.");

    public PublishOriginFamily Origin { get; }
}

internal sealed class PublishRoute<T> : PublishRoute where T : class
{
    public PublishRoute(
        SyncDataType[] dataTypes,
        PublishOriginFamily origin,
        Func<IConnectorPublisher, IEnumerable<T>, string, WriteOrigin, CancellationToken, Task<bool>> publish,
        Action<T, BaseConnectorConfiguration>? prepare = null)
        : base(typeof(T), dataTypes, origin)
    {
        Publish = publish;
        Prepare = prepare;
    }

    public Func<IConnectorPublisher, IEnumerable<T>, string, WriteOrigin, CancellationToken, Task<bool>> Publish { get; }

    /// <summary>Runs on each record before it is published, for what only the connector's config knows.</summary>
    public Action<T, BaseConnectorConfiguration>? Prepare { get; }
}

/// <summary>
///     The one place a record type is paired with its <see cref="SyncDataType"/>, its origin family
///     and its publisher member. A record type with no entry here cannot be published.
/// </summary>
internal static class PublishRoutes
{
    private static readonly Dictionary<Type, PublishRoute> Table = Build(
        Route<Entry>([SyncDataType.Glucose], PublishOriginFamily.Glucose,
            (p, r, s, o, ct) => p.Glucose.PublishEntriesAsync(r, s, o, ct)),
        Route<SensorGlucose>([SyncDataType.Glucose], PublishOriginFamily.Glucose,
            (p, r, s, o, ct) => p.Glucose.PublishSensorGlucoseAsync(r, s, o, ct),
            prepare: (record, config) =>
            {
                record.GlucoseProcessing = config.GlucoseProcessing;
                record.SmoothedMgdl ??= config.GlucoseProcessing == GlucoseProcessing.Smoothed ? record.Mgdl : null;
            }),
        Route<Treatment>([], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishTreatmentsAsync(r, s, o, ct)),
        Route<Bolus>([SyncDataType.Boluses], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishBolusesAsync(r, s, o, ct)),
        Route<CarbIntake>([SyncDataType.CarbIntake], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishCarbIntakesAsync(r, s, o, ct)),
        Route<BGCheck>([SyncDataType.ManualBG], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishBGChecksAsync(r, s, o, ct)),
        Route<BolusCalculation>([SyncDataType.BolusCalculations], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishBolusCalculationsAsync(r, s, o, ct)),
        Route<TempBasal>([SyncDataType.TempBasals, SyncDataType.StateSpans], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishTempBasalsAsync(r, s, o, ct)),
        Route<BasalInjection>([SyncDataType.BasalInjections], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Treatments.PublishBasalInjectionsAsync(r, s, o, ct)),
        Route<Note>([SyncDataType.Notes], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Metadata.PublishNotesAsync(r, s, o, ct)),
        Route<DeviceStatus>([SyncDataType.DeviceStatus], PublishOriginFamily.Device,
            (p, r, s, o, ct) => p.Device.PublishDeviceStatusAsync(r, s, o, ct)),
        Route<DeviceEvent>([SyncDataType.DeviceEvents], PublishOriginFamily.Treatment,
            (p, r, s, o, ct) => p.Device.PublishDeviceEventsAsync(r, s, o, ct)),
        // System events have no sync data type of their own.
        Route<SystemEvent>([SyncDataType.DeviceEvents], PublishOriginFamily.Live,
            (p, r, s, o, ct) => p.Metadata.PublishSystemEventsAsync(r, s, o, ct)),
        Route<Profile>([SyncDataType.Profiles], PublishOriginFamily.Live,
            (p, r, s, o, ct) => p.Metadata.PublishProfilesAsync(r, s, o, ct)),
        Route<Food>([SyncDataType.Food], PublishOriginFamily.Live,
            (p, r, s, o, ct) => p.Metadata.PublishFoodAsync(r, s, o, ct)),
        Route<ConnectorFoodEntryImport>([SyncDataType.Food], PublishOriginFamily.Live,
            async (p, r, s, o, ct) => await p.Metadata.PublishConnectorFoodEntriesAsync(r, s, o, ct) is not null),
        Route<Activity>([SyncDataType.Activity], PublishOriginFamily.Live,
            (p, r, s, o, ct) => p.Metadata.PublishActivityAsync(r, s, o, ct)),
        Route<StateSpan>([SyncDataType.StateSpans], PublishOriginFamily.Live,
            (p, r, s, o, ct) => p.Metadata.PublishStateSpansAsync(r, s, o, ct)),
        // Profile activations replace the Profile spans, which gated under StateSpans.
        Route<ProfileActivation>([SyncDataType.StateSpans], PublishOriginFamily.Live,
            (p, r, s, o, ct) => p.Metadata.PublishProfileActivationsAsync(r, s, o, ct)),
        Route<BodyWeight>([SyncDataType.BodyWeight], PublishOriginFamily.Live,
            (p, r, s, _, ct) => p.Metadata.PublishBodyWeightsAsync(r, s, ct)),
        Route<StepCount>([SyncDataType.Steps], PublishOriginFamily.Live,
            (p, r, s, _, ct) => p.Metadata.PublishStepCountsAsync(r, s, ct)),
        Route<HeartRate>([SyncDataType.HeartRate], PublishOriginFamily.Live,
            (p, r, s, _, ct) => p.Metadata.PublishHeartRatesAsync(r, s, ct)));

    public static IReadOnlyCollection<PublishRoute> All => Table.Values;

    /// <exception cref="InvalidOperationException">No route publishes <typeparamref name="T"/>.</exception>
    public static PublishRoute<T> For<T>() where T : class =>
        Table.TryGetValue(typeof(T), out var route)
            ? (PublishRoute<T>)route
            : throw new InvalidOperationException(
                $"{typeof(T).Name} has no publish route. Add it to {nameof(PublishRoutes)}.");

    private static PublishRoute Route<T>(
        SyncDataType[] dataTypes,
        PublishOriginFamily origin,
        Func<IConnectorPublisher, IEnumerable<T>, string, WriteOrigin, CancellationToken, Task<bool>> publish,
        Action<T, BaseConnectorConfiguration>? prepare = null) where T : class =>
        new PublishRoute<T>(dataTypes, origin, publish, prepare);

    private static Dictionary<Type, PublishRoute> Build(params PublishRoute[] routes) =>
        routes.ToDictionary(route => route.RecordType);
}

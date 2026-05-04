using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Aggregates the data-source dependencies needed to populate a <see cref="Nocturne.Core.Models.SensorContext"/>.
/// Bundled to keep the <see cref="SensorContextEnricher"/> constructor focused on coordination,
/// not plumbing — the enricher accumulates dependencies as the alert engine grows, and a
/// flat 12-arg ctor stops being defensible somewhere around eight.
/// </summary>
/// <remarks>
/// Registered as a scoped service. The DI container resolves each member from the same
/// scope as the enricher itself, so lifetimes match what direct constructor injection
/// would produce.
/// </remarks>
internal sealed record SensorContextEnricherDependencies(
    IIobCalculator Iob,
    ICobCalculator Cob,
    ITreatmentService Treatments,
    IBolusRepository Boluses,
    ICarbIntakeRepository CarbIntakes,
    IDeviceEventRepository DeviceEvents,
    IPumpSnapshotRepository PumpSnapshots,
    IApsSnapshotRepository ApsSnapshots,
    ITempBasalRepository TempBasals,
    IUploaderSnapshotRepository UploaderSnapshots,
    IStateSpanService StateSpans,
    IAlertRepository Alerts,
    ITargetRangeScheduleRepository TargetRangeSchedules,
    IActiveProfileResolver ActiveProfileResolver,
    ITherapySettingsResolver TherapySettings,
    IOptions<AlertEvaluationOptions> Options);
